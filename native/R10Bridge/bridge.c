/* R10 bridge: no game addresses, no background VM calls, integer-only HLL ABI. */
#define _WIN32_WINNT 0x0601
#include <windows.h>

#define BUILD "r10-bridge-v1"
#define HISTORY 16
#define EXECUTING 4 /* Internal state; externally still pending until acknowledged. */
#define CARD_CAPABILITY 512
#define CARD_PAGE_SIZE 32
#define CARD_ROW_LIMIT 4096
#define CARD_TEXT_BYTES 64
#define CARD_KEY_SIZE (CARD_TEXT_BYTES*4+2)
enum { CARD_PAGE=10, CARD_INSPECT, CARD_GRANT, CARD_SET_STAR };
typedef struct { LONG id, generation, enabled, state, kind, argument, argument2, context; DWORD submitted; } Command;
typedef struct {
    LONG index, flags;
    unsigned idLength, characterLength;
    unsigned char id[CARD_TEXT_BYTES], character[CARD_TEXT_BYTES];
} CardRow;
typedef struct {
    LONG total, count, star, exp, nextExp, maxStar;
    int invalid;
    char expected[CARD_KEY_SIZE];
    CardRow rows[CARD_PAGE_SIZE];
} CardResult;
typedef struct {
    DWORD pid, gameThreadId, heartbeat, frame, lastTick;
    LONG generation, phase, hpGuard, playerHp, playerHpMax, enemyHp, enemyHpMax;
    LONG scriptVersion, capabilities, loadKind, loadSlot, lastCompletedGeneration, lastLoadOutcome;
    LONG blockedHits, lastRequestedHp, lastCommandId, lastCommandState;
    LONG pendingId, nextId;
    LONG noDuplicateCards, cardBatch, cardBatchEnabled, filteredCards, fallbackCards;
    LONG foodRedraw, autoRedistribute;
    LONG resourcesFull, dataReady, food, medal, friendship, unit;
    LONG cardReady, cardContext;
    LONG selector, controlEnabled, battleReady; DWORD battleTick;
    Command commands[HISTORY];
} State;
static State state;
static CardResult cardResults[HISTORY];
static CRITICAL_SECTION gate;
static volatile LONG initialized;
static char pipeName[80];
static DWORD WINAPI server(void *unused);

static char *put(char *p, const char *s) { while (*s) *p++ = *s++; return p; }
static char *number(char *p, LONG value) {
    char digits[12]; unsigned n = value < 0 ? 0u - (unsigned)value : (unsigned)value;
    unsigned at = 0; if (value < 0) *p++ = '-';
    do { digits[at++] = (char)('0' + n % 10); n /= 10; } while (n);
    while (at) *p++ = digits[--at]; return p;
}
static char *unsignedNumber(char *p, DWORD value) {
    char digits[12]; unsigned at=0;
    do { digits[at++]=(char)('0'+value%10);value/=10; } while(value);
    while(at)*p++=digits[--at];return p;
}
static int equal(const char *a, const char *b) {
    while (*a && *a == *b) { a++; b++; } return *a == *b;
}
static int cardCommand(LONG kind) { return kind>=CARD_PAGE && kind<=CARD_SET_STAR; }
static LONG capability(LONG kind) { return cardCommand(kind) ? CARD_CAPABILITY : kind==1 ? 4 : kind==5 ? 32 : kind==6 ? 64 : kind==7 ? 128 : kind==8 || kind==9 ? 256 : 16; }
static LONG dataFeature(const char *name) { return equal(name,"food") ? 1 : equal(name,"medal") ? 2 : equal(name,"friendship") ? 3 : equal(name,"unit") ? 4 : equal(name,"star") ? 5 : 0; }
static void copy(void *to, const void *from, unsigned n) {
    unsigned char *d = to; const unsigned char *s = from; while (n--) *d++ = *s++;
}
static void zero(void *to, unsigned n) { unsigned char *d = to; while (n--) *d++ = 0; }
static const char *commandState(LONG n) { return n == 1 || n == EXECUTING ? "queued" : n == 2 ? "applied" : n == 3 ? "rejected" : "none"; }
static char *prefix(char *p) { return put(p, "{\"ok\":true,\"protocol\":1,\"build\":\"" BUILD "\""); }
static char *field(char *p, const char *name, LONG value) { p = put(p,name); return number(p,value); }
static char *hexBytes(char *p,const unsigned char *bytes,unsigned length) {
    static const char hex[]="0123456789ABCDEF";
    for (unsigned i=0;i<length;i++) { *p++=hex[bytes[i]>>4]; *p++=hex[bytes[i]&15]; }
    return p;
}
static char *cardPayload(char *p,const Command *command,const CardResult *result) {
    p=field(p,",\"cardTotal\":",result->total);
    p=put(p,",\"cardRows\":[");
    for (LONG i=0;i<result->count;i++) {
        const CardRow *row=&result->rows[i];
        p=field(p,i ? ",{\"index\":" : "{\"index\":",row->index);
        p=field(p,",\"flags\":",row->flags);
        p=put(p,",\"idHex\":\""); p=hexBytes(p,row->id,row->idLength);
        p=put(p,"\",\"characterHex\":\""); p=hexBytes(p,row->character,row->characterLength);
        p=put(p,"\"}");
    }
    p=put(p,"]");
    if (command->kind!=CARD_PAGE) {
        p=field(p,",\"star\":",result->star); p=field(p,",\"exp\":",result->exp);
        p=field(p,",\"nextExp\":",result->nextExp); p=field(p,",\"maxStar\":",result->maxStar);
    }
    return p;
}
static void errorResponse(char *out, const char *error) {
    char *p = put(out,"{\"ok\":false,\"protocol\":1,\"build\":\"" BUILD "\",\"error\":\"");
    p = put(p,error); p = put(p,"\"}\n"); *p = 0;
}

/* Strict bounded flat JSON: protocol fields are ASCII strings, booleans, integers. */
typedef struct {
    char op[32], feature[32], key[CARD_KEY_SIZE];
    LONG enabled, generation, id, action, argument, argument2, value, context;
    unsigned present;
} Request;
static void whitespace(const char **s) { while (**s == ' ' || **s == '\t' || **s == '\r' || **s == '\n') ++*s; }
static int string(const char **s, char *dest, unsigned capacity) {
    unsigned n = 0; if (*(*s)++ != '"') return 0;
    while (**s && **s != '"') {
        unsigned char c = (unsigned char)*(*s)++;
        if (c < 32 || c >= 127 || c == '\\' || n + 1 >= capacity) return 0;
        dest[n++] = (char)c;
    }
    if (**s != '"') return 0; ++*s; dest[n] = 0; return 1;
}
static int integer(const char **s, LONG *dest) {
    unsigned n = 0, digits = 0;
    if (**s < '0' || **s > '9') return 0;
    if (**s == '0' && (*s)[1] >= '0' && (*s)[1] <= '9') return 0;
    while (**s >= '0' && **s <= '9') {
        unsigned digit = (unsigned)(*(*s)++ - '0');
        if (++digits > 10 || n > 214748364u || (n == 214748364u && digit > 7)) return 0;
        n = n * 10 + digit;
    }
    *dest = (LONG)n; return 1;
}
static int literal(const char **s, const char *word) {
    const char *p=*s;
    while (*word) { if (*p++ != *word++) return 0; }
    *s=p; return 1;
}
static int parse(const char *s, Request *r) {
    zero(r,sizeof(*r)); whitespace(&s); if (*s++ != '{') return 0;
    for (;;) {
        char key[32]; unsigned mask; whitespace(&s);
        if (!string(&s,key,sizeof(key))) return 0;
        whitespace(&s); if (*s++ != ':') return 0; whitespace(&s);
        mask = equal(key,"op") ? 1 : equal(key,"feature") ? 2 : equal(key,"enabled") ? 4 : equal(key,"generation") ? 8 : equal(key,"id") ? 16 : equal(key,"action") ? 32 : equal(key,"argument") ? 64 : equal(key,"argument2") ? 128 : equal(key,"value") ? 256 : equal(key,"context") ? 512 : equal(key,"key") ? 1024 : 0;
        if (!mask || r->present & mask) return 0; r->present |= mask;
        if (mask == 1 || mask == 2) { if (!string(&s,mask == 1 ? r->op : r->feature,32)) return 0; }
        else if (mask == 1024) { if (!string(&s,r->key,sizeof(r->key))) return 0; }
        else if (mask == 4) {
            if (literal(&s,"true")) r->enabled=1;
            else if (literal(&s,"false")) r->enabled=0;
            else return 0;
        } else if (!integer(&s,mask == 8 ? &r->generation : mask == 16 ? &r->id : mask == 32 ? &r->action : mask == 64 ? &r->argument : mask == 256 ? &r->value : mask == 512 ? &r->context : &r->argument2)) return 0;
        whitespace(&s); if (*s == '}') { s++; break; } if (*s++ != ',') return 0;
    }
    whitespace(&s); return !*s && (r->present & 1);
}
static void respond(const char *input, char *out) {
    Request r; State snapshot; char *p; LONG id;
    if (!parse(input,&r)) { errorResponse(out,"invalid_request"); return; }
    int cardRequest=equal(r.op,"card");
    if (cardRequest || equal(r.op,"set_feature") || equal(r.op,"action") || equal(r.op,"set_value")) {
        LONG kind=equal(r.op,"set_value") ? 8 : cardRequest || equal(r.op,"action") ? r.action : equal(r.feature,"resources_full") ? 9 : equal(r.feature,"no_duplicate_cards") ? 5 : equal(r.feature,"food_redraw") ? 6 : equal(r.feature,"auto_redistribute") ? 7 : equal(r.feature,"hp_guard") ? 1 : 0;
        if (cardRequest && (!cardCommand(kind) || r.present!=(1|8|32|64|128|512|1024) ||
            !r.context || r.argument>=CARD_ROW_LIMIT || (kind!=CARD_SET_STAR && r.argument2!=0) ||
            (kind==CARD_PAGE ? r.key[0]!=0 : r.key[0]==0))) { errorResponse(out,"invalid_card_request"); return; }
        if (equal(r.op,"set_feature") && (r.present != 15 || !kind)) { errorResponse(out,"unsupported_feature"); return; }
        if (kind==8) {
            r.argument=dataFeature(r.feature); r.argument2=r.value;
            if (r.present!=267 || !r.argument || (r.argument<=3 && r.value>3) || (r.argument==4 && r.value>999) ||
                (r.argument==5 && r.value!=10 && r.value!=40 && r.value!=80 && r.value!=200)) { errorResponse(out,"invalid_value"); return; }
        }
        if (equal(r.op,"action") && (r.present != 233 || kind<2 || kind>4 ||
            (kind==2 && (r.argument>9998 || r.argument2)) ||
            (kind==3 && (r.argument || r.argument2)) || (kind==4 && (r.argument>6 || r.argument2>1)))) { errorResponse(out,"invalid_action"); return; }
        EnterCriticalSection(&gate);
        if (state.scriptVersion!=1 || !(state.capabilities & capability(kind))) { LeaveCriticalSection(&gate); errorResponse(out,"script_hook_unavailable"); return; }
        if (r.generation != state.generation) { LeaveCriticalSection(&gate); errorResponse(out,"generation_mismatch"); return; }
        if (state.phase != 1 || GetTickCount()-state.lastTick > 2000) { LeaveCriticalSection(&gate); errorResponse(out,"game_not_ready"); return; }
        if (cardRequest && (!state.cardReady || r.context!=state.cardContext)) { LeaveCriticalSection(&gate); errorResponse(out,"card_context_changed"); return; }
        if ((kind==8 || (kind==9 && r.enabled)) && !state.dataReady) { LeaveCriticalSection(&gate); errorResponse(out,"data_not_ready"); return; }
        if (kind==8 && r.argument<=3 && state.resourcesFull) { LeaveCriticalSection(&gate); errorResponse(out,"resources_full_enabled"); return; }
        if (kind>=3 && kind<=4 && (!state.battleReady || GetTickCount()-state.battleTick>250)) { LeaveCriticalSection(&gate); errorResponse(out,"battle_controls_unavailable"); return; }
        if (state.pendingId) { LeaveCriticalSection(&gate); errorResponse(out,"command_pending"); return; }
        if (state.nextId == 2147483647) { LeaveCriticalSection(&gate); errorResponse(out,"command_id_exhausted"); return; }
        id = ++state.nextId;
        Command *c = &state.commands[id % HISTORY];
        c->id=id; c->generation=r.generation; c->enabled=r.enabled; c->state=1;
        c->kind=kind; c->argument=r.argument; c->argument2=r.argument2; c->submitted=GetTickCount();
        c->context=r.context;
        if (cardRequest) {
            CardResult *result=&cardResults[id % HISTORY];
            result->count=0; result->total=0; result->star=-1; result->exp=0;
            result->nextExp=0; result->maxStar=0; result->invalid=0;
            copy(result->expected,r.key,sizeof(result->expected));
        }
        state.pendingId=id; state.lastCommandId=id; state.lastCommandState=1;
        LeaveCriticalSection(&gate);
        p=prefix(out); p=field(p,",\"id\":",id); p=put(p,",\"state\":\"queued\"}\n"); *p=0; return;
    }
    if (equal(r.op,"get_command_result")) {
        if (r.present != 17 || r.id <= 0) { errorResponse(out,"invalid_request"); return; }
        EnterCriticalSection(&gate); Command c = state.commands[r.id % HISTORY];
        if (c.id != r.id) { LeaveCriticalSection(&gate); errorResponse(out,"command_not_found"); return; }
        p=prefix(out); p=field(p,",\"id\":",c.id); p=put(p,",\"state\":\""); p=put(p,commandState(c.state));
        p=put(p,"\""); if (c.state==3) p=put(p,",\"error\":\"action_unavailable_or_context_changed\"");
        if (c.state==2 && cardCommand(c.kind)) p=cardPayload(p,&c,&cardResults[r.id % HISTORY]);
        LeaveCriticalSection(&gate);
        p=put(p,"}\n"); *p=0; return;
    }
    if (r.present != 1 || (!equal(r.op,"get_state") && !equal(r.op,"ping"))) { errorResponse(out,"unsupported_operation"); return; }
    EnterCriticalSection(&gate); copy(&snapshot,&state,sizeof(state)); LeaveCriticalSection(&gate);
    p=prefix(out); p=field(p,",\"pid\":",snapshot.pid); p=field(p,",\"gameThreadId\":",snapshot.gameThreadId);
    p=put(p,",\"heartbeat\":"); p=unsignedNumber(p,snapshot.heartbeat); p=put(p,",\"frame\":"); p=unsignedNumber(p,snapshot.frame);
    p=put(p,",\"heartbeatAgeMs\":"); p=unsignedNumber(p,GetTickCount()-snapshot.lastTick);
    p=field(p,",\"loadGeneration\":",snapshot.generation); p=field(p,",\"phase\":",snapshot.phase);
    p=field(p,",\"scriptVersion\":",snapshot.scriptVersion); p=field(p,",\"capabilities\":",snapshot.capabilities);
    p=put(p,snapshot.battleReady && GetTickCount()-snapshot.battleTick<=250 ? ",\"battleReady\":true" : ",\"battleReady\":false");
    p=field(p,",\"loadKind\":",snapshot.loadKind); p=field(p,",\"loadSlot\":",snapshot.loadSlot);
    p=field(p,",\"lastCompletedGeneration\":",snapshot.lastCompletedGeneration); p=field(p,",\"lastLoadOutcome\":",snapshot.lastLoadOutcome);
    p=put(p,snapshot.noDuplicateCards ? ",\"noDuplicateCards\":true" : ",\"noDuplicateCards\":false");
    p=put(p,snapshot.foodRedraw ? ",\"foodRedraw\":true" : ",\"foodRedraw\":false");
    p=put(p,snapshot.autoRedistribute ? ",\"autoRedistribute\":true" : ",\"autoRedistribute\":false");
    p=put(p,snapshot.resourcesFull ? ",\"resourcesFull\":true" : ",\"resourcesFull\":false");
    p=put(p,snapshot.dataReady ? ",\"dataReady\":true" : ",\"dataReady\":false");
    p=field(p,",\"cardContext\":",snapshot.cardReady ? snapshot.cardContext : 0);
    p=field(p,",\"food\":",snapshot.food); p=field(p,",\"medal\":",snapshot.medal);
    p=field(p,",\"friendship\":",snapshot.friendship); p=field(p,",\"unit\":",snapshot.unit);
    p=field(p,",\"filteredCards\":",snapshot.filteredCards); p=field(p,",\"fallbackCards\":",snapshot.fallbackCards);
    p=put(p,snapshot.hpGuard ? ",\"hpGuard\":true" : ",\"hpGuard\":false");
    p=put(p,",\"playerHp\":"); p=snapshot.playerHp < 0 ? put(p,"null") : number(p,snapshot.playerHp);
    p=put(p,",\"playerHpMax\":"); p=snapshot.playerHpMax < 0 ? put(p,"null") : number(p,snapshot.playerHpMax);
    p=put(p,",\"enemyHp\":"); p=snapshot.enemyHp < 0 ? put(p,"null") : number(p,snapshot.enemyHp);
    p=field(p,",\"blockedHits\":",snapshot.blockedHits); p=field(p,",\"lastRequestedHp\":",snapshot.lastRequestedHp);
    p=field(p,",\"lastCommandId\":",snapshot.lastCommandId); p=put(p,",\"lastCommandState\":\""); p=put(p,commandState(snapshot.lastCommandState));
    p=put(p,"\"}\n"); *p=0;
}
static int finishIo(HANDLE pipe, OVERLAPPED *o, BOOL immediate, DWORD *count, DWORD timeout) {
    if (immediate) return 1;
    if (GetLastError()!=ERROR_IO_PENDING) return 0;
    if (WaitForSingleObject(o->hEvent,timeout)!=WAIT_OBJECT_0) {
        CancelIoEx(pipe,o); GetOverlappedResult(pipe,o,count,TRUE); return 0;
    }
    return GetOverlappedResult(pipe,o,count,FALSE)!=0;
}
static DWORD WINAPI server(void *unused) {
    (void)unused;
    for (;;) {
        HANDLE pipe=CreateNamedPipeA(pipeName,PIPE_ACCESS_DUPLEX|FILE_FLAG_OVERLAPPED|FILE_FLAG_FIRST_PIPE_INSTANCE,
            PIPE_TYPE_BYTE|PIPE_READMODE_BYTE|PIPE_WAIT|PIPE_REJECT_REMOTE_CLIENTS,1,4096,4096,1000,NULL);
        if (pipe==INVALID_HANDLE_VALUE) return 1;
        OVERLAPPED o; zero(&o,sizeof(o)); o.hEvent=CreateEventA(NULL,TRUE,FALSE,NULL);
        if (!o.hEvent) { CloseHandle(pipe); return 2; }
        DWORD count=0; BOOL connected=ConnectNamedPipe(pipe,&o);
        DWORD connectError=connected ? 0 : GetLastError();
        if (connected || connectError==ERROR_PIPE_CONNECTED || (connectError==ERROR_IO_PENDING && finishIo(pipe,&o,FALSE,&count,INFINITE))) {
            /* A full card page contains at most two 64-byte hex fields per row. */
            static char output[CARD_PAGE_SIZE*(4*CARD_TEXT_BYTES+96)+2048];
            char input[1024]; unsigned used=0; int complete=0,invalid=0;
            DWORD requestStarted=GetTickCount();
            while (used < sizeof(input)-1) {
                DWORD elapsed=GetTickCount()-requestStarted;
                if (elapsed>=1000) break;
                ResetEvent(o.hEvent); count=0;
                if (!finishIo(pipe,&o,ReadFile(pipe,input+used,sizeof(input)-1-used,&count,&o),&count,1000-elapsed) || !count) break;
                unsigned start=used; used+=count;
                for (unsigned i=start;i<used;i++) {
                    if (!input[i]) invalid=1;
                    if (input[i]=='\n') { used=i; complete=1; break; }
                }
                if (complete) break;
            }
            if (complete) {
                input[used]=0;
                if (invalid) errorResponse(output,"invalid_request"); else respond(input,output);
                unsigned n=0; while(output[n])n++;
                ResetEvent(o.hEvent); count=0;
                if (finishIo(pipe,&o,WriteFile(pipe,output,n,&count,&o),&count,1000) && count==n) {
                    /* Give the peer time to consume the line and close; never block the game thread. */
                    DWORD until=GetTickCount();
                    while (GetTickCount()-until<1000) {
                        if (!PeekNamedPipe(pipe,NULL,0,NULL,NULL,NULL)) break;
                        Sleep(1);
                    }
                }
            }
        }
        CancelIoEx(pipe,NULL); DisconnectNamedPipe(pipe); CloseHandle(o.hEvent); CloseHandle(pipe);
    }
}
int __cdecl Tick(int marker,int a,int b) {
    if (InterlockedCompareExchange(&initialized,1,0)==0) {
        HMODULE module=NULL;
        if (!GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS|GET_MODULE_HANDLE_EX_FLAG_PIN,(const char *)&Tick,&module)) { InterlockedExchange(&initialized,-1); return -1; }
        InitializeCriticalSection(&gate);
        state.cardBatch=-1;
        state.pid=GetCurrentProcessId(); state.gameThreadId=GetCurrentThreadId();
        state.playerHp=state.playerHpMax=state.enemyHp=state.enemyHpMax=-1;
        char *p=put(pipeName,"\\\\.\\pipe\\R10Bridge."); p=number(p,state.pid); *p=0;
        HANDLE thread=CreateThread(NULL,0,server,NULL,0,NULL);
        if (!thread) { InterlockedExchange(&initialized,-1); return -2; }
        CloseHandle(thread); InterlockedExchange(&initialized,2);
    }
    if (initialized != 2 || GetCurrentThreadId()!=state.gameThreadId) return -3;
    EnterCriticalSection(&gate);
    state.heartbeat++; state.lastTick=GetTickCount();
    if (marker==0) { state.dataReady=0; state.cardReady=0; state.cardBatch=-1; state.cardBatchEnabled=0; state.scriptVersion=a; state.capabilities=b; state.controlEnabled=state.battleReady=0; }
    else if (marker==1) { state.frame++; if (state.phase==0) state.phase=1; }
    else if (marker==2) {
        state.dataReady=0; state.cardReady=0;
        state.cardBatch=-1; state.cardBatchEnabled=0;
        state.generation++; state.phase=2; state.loadKind=a; state.loadSlot=b; state.lastLoadOutcome=0;
        state.playerHp=state.playerHpMax=state.enemyHp=state.enemyHpMax=-1;
        state.controlEnabled=state.battleReady=0;
    } else if (marker==3 && state.phase==2 && state.loadKind==a) { state.phase=1; state.lastCompletedGeneration=state.generation; state.lastLoadOutcome=1; }
    else if (marker==4 && state.phase==2 && state.loadKind==a) { state.phase=0; state.lastLoadOutcome=2; }
    else if (marker==10) { state.playerHp=a; state.playerHpMax=b; }
    else if (marker==11) { state.enemyHp=a; state.enemyHpMax=b; }
    else if (marker==12) { state.playerHp=state.playerHpMax=state.enemyHp=state.enemyHpMax=-1; }
    else if (marker==20) { state.selector=a; state.controlEnabled=b; if (!b) state.battleReady=0; }
    if (marker==1 && state.pendingId) {
        Command *c=&state.commands[state.pendingId % HISTORY];
        if (c->generation!=state.generation || state.phase!=1 || state.scriptVersion!=1 || !(state.capabilities & capability(c->kind)) || (c->state!=EXECUTING && GetTickCount()-c->submitted>3000)) c->state=3;
        else if (c->kind==5) { state.noDuplicateCards=c->enabled; c->state=2; }
        else if (c->kind==6) { state.foodRedraw=c->enabled; c->state=2; }
        else if (c->kind==7) { state.autoRedistribute=c->enabled; c->state=2; }
        else if (c->kind==9 && !c->enabled) { state.resourcesFull=0; c->state=2; }
        else if (c->kind==1 && state.scriptVersion==1 && (state.capabilities & 4)) { state.hpGuard=c->enabled; c->state=2; }
        if (c->state!=1 && c->state!=EXECUTING) { state.lastCommandState=c->state; state.pendingId=0; }
    }
    LONG result=state.generation; LeaveCriticalSection(&gate); return result;
}
/* Script-only integer ABI. Commands are consumed exclusively on the game thread. */
int __cdecl Control(int op,int a,int b) {
    LONG result=0;
    if (initialized!=2 || GetCurrentThreadId()!=state.gameThreadId) return 0;
    EnterCriticalSection(&gate);
    Command *c=state.pendingId ? &state.commands[state.pendingId % HISTORY] : NULL;
    if (op==4) result=state.selector==a && state.controlEnabled;
    else if (op==5) { state.battleReady=a; state.battleTick=GetTickCount(); }
    else if (c && c->state==1 && c->generation==state.generation && state.phase==1 && c->kind>=2 && c->kind<=4) {
        if (op==0) result=c->kind;
        else if (op==1) result=c->argument;
        else if (op==2) result=c->argument2;
        else if (op==3) {
            c->state=a ? 2 : 3; state.lastCommandState=c->state; state.pendingId=0;
            if (a && c->kind>=3) state.controlEnabled=state.battleReady=0;
        }
    }
    (void)b; LeaveCriticalSection(&gate); return result;
}
/* Only call this export from a damage-specific script wrapper, never initialization setters. */
int __cdecl FilterHp(int side,int oldHp,int requestedHp) {
    int result=requestedHp;
    if (initialized!=2 || GetCurrentThreadId()!=state.gameThreadId) return result;
    EnterCriticalSection(&gate);
    if (state.scriptVersion==1 && (state.capabilities & 4) && state.phase==1 && state.hpGuard && side==0 && oldHp>0 && requestedHp<oldHp) {
        result=oldHp; state.blockedHits++; state.lastRequestedHp=requestedHp;
    }
    LeaveCriticalSection(&gate); return result;
}
BOOL WINAPI DllMain(HINSTANCE instance,DWORD reason,LPVOID reserved) { (void)instance;(void)reason;(void)reserved;return TRUE; }

int __cdecl Feature(int kind,int a,int b) {
    (void)a; (void)b;
    if (initialized!=2 || GetCurrentThreadId()!=state.gameThreadId) return 0;
    EnterCriticalSection(&gate);
    int result=state.phase==1 && state.scriptVersion==1 && (state.capabilities & capability(kind)) &&
        ((kind==6 && state.foodRedraw) || (kind==7 && state.autoRedistribute));
    LeaveCriticalSection(&gate); return result;
}

/* Claim once on the game thread. The pending slot pins immutable arguments until acknowledgement;
   its queue deadline must never substitute zero while the synchronous AIN setter is executing. */
int __cdecl Data(int op,int a,int b) {
    (void)b;
    if (initialized!=2 || GetCurrentThreadId()!=state.gameThreadId) return 0;
    EnterCriticalSection(&gate);
    LONG result=0;
    Command *c=state.pendingId ? &state.commands[state.pendingId % HISTORY] : NULL;
    int ready=state.phase==1 && state.scriptVersion==1 && (state.capabilities & 256);
    if (op==6) state.dataReady=ready && a;
    else if (ready) {
        if (op==7) result=state.resourcesFull;
        else if (op==10) state.food=a;
        else if (op==11) state.medal=a;
        else if (op==12) state.friendship=a;
        else if (op==13) state.unit=a;
    }
    if (c && (c->state==1 || c->state==EXECUTING) && (c->kind==8 || c->kind==9)) {
        int valid=ready && c->generation==state.generation && (c->state==EXECUTING || GetTickCount()-c->submitted<=3000);
        if (!valid) { c->state=3; state.lastCommandState=3; state.pendingId=0; }
        else if (op==0) { c->state=EXECUTING; result=c->kind==9 ? 6 : c->argument; }
        else if (op==1 && c->state==EXECUTING) result=c->argument2;
        else if (op==2) {
            c->state=a ? 2 : 3;
            if (a && c->kind==9) state.resourcesFull=c->enabled;
            state.lastCommandState=c->state; state.pendingId=0;
        }
    }
    LeaveCriticalSection(&gate); return result;
}

int __cdecl Cards(int op,int a,int b) {
    (void)b;
    if (initialized!=2 || GetCurrentThreadId()!=state.gameThreadId) return 0;
    EnterCriticalSection(&gate);
    LONG result=0;
    if (op==0) result=state.cardBatchEnabled && state.cardBatch>=0;
    else if (op==1) { state.cardBatch=a; state.cardBatchEnabled=state.noDuplicateCards && (state.capabilities & 32); }
    else if (op==2) { state.cardBatch=-1; state.cardBatchEnabled=0; }
    else if (op==3) result=state.cardBatch;
    else if (op==4) state.filteredCards++;
    else if (op==5) state.fallbackCards++;
    LeaveCriticalSection(&gate); return result;
}

static void cardText(CardResult *result,unsigned char *bytes,unsigned *length,int value) {
    unsigned count=value>255 ? 2u : 1u;
    if (value< -128 || value>65535 || !(value&255) || *length+count>CARD_TEXT_BYTES) {
        result->invalid=1; return;
    }
    // The supported System43 VM packs a double-byte C_REF result with its lead byte high.
    if (count==2) bytes[(*length)++]=(unsigned char)((unsigned)value>>8);
    bytes[(*length)++]=(unsigned char)value;
}

/* The AIN copies character units, never a VM string handle. The original card and
   character IDs are matched against the displayed row before a mutation starts. */
int __cdecl CardEdit(int op,int a,int b) {
    if (initialized!=2 || GetCurrentThreadId()!=state.gameThreadId) return 0;
    EnterCriticalSection(&gate);
    LONG reply=0;
    Command *c=state.pendingId ? &state.commands[state.pendingId % HISTORY] : NULL;
    int available=state.phase==1 && state.scriptVersion==1 && (state.capabilities & CARD_CAPABILITY);
    if (op==0) {
        int ready=available && a && state.cardContext<2147483647;
        if (ready && !state.cardReady) ++state.cardContext;
        state.cardReady=ready;
    } else if (c && cardCommand(c->kind) && c->generation==state.generation) {
        CardResult *result=&cardResults[c->id % HISTORY];
        if (op==1 && c->state==1) {
            if (!available || !state.cardReady || c->context!=state.cardContext || GetTickCount()-c->submitted>3000) {
                c->state=3; state.lastCommandState=3; state.pendingId=0;
            } else { c->state=EXECUTING; reply=c->kind; }
        } else if (c->state==EXECUTING) {
            CardRow *row=result->count ? &result->rows[result->count-1] : NULL;
            if (op==2) reply=c->argument;
            else if (op==3) reply=c->argument2;
            else if (op==4) {
                if (result->count>=CARD_PAGE_SIZE || a<0 || a>=CARD_ROW_LIMIT || b<0 || b>7) result->invalid=1;
                else {
                    row=&result->rows[result->count++]; row->index=a; row->flags=b;
                    row->idLength=row->characterLength=0;
                }
            } else if (op==5 && row) cardText(result,row->id,&row->idLength,a);
            else if (op==6 && row) cardText(result,row->character,&row->characterLength,a);
            else if (op==7 && row && result->count==1 && row->idLength && !result->invalid &&
                     available && state.cardReady && c->context==state.cardContext) {
                char actual[CARD_KEY_SIZE];
                char *p=hexBytes(actual,row->id,row->idLength); *p++='.';
                p=hexBytes(p,row->character,row->characterLength); *p=0;
                reply=equal(actual,result->expected);
            } else if (op==8 && row) row->flags|=1;
            else if (op==9) { result->star=a; result->exp=b; }
            else if (op==10) { result->nextExp=a; result->maxStar=b; }
            else if (op==11) {
                result->total=b;
                c->state=a && !result->invalid && available && c->context==state.cardContext ? 2 : 3;
                state.lastCommandState=c->state; state.pendingId=0;
            }
        }
    }
    LeaveCriticalSection(&gate); return reply;
}
