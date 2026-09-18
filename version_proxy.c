// Valheim AutoModSync client bootstrap / version.dll proxy
// x64 Windows, no CRT imports. Built with clang + lld-link.
// 2.2.2: resolves forwarded Kernel32 exports and defers System32 version.dll loading until after DllMain.
// Purpose: one-file client bootstrap. The server installer appends a trusted
// bootstrap payload containing BepInEx (install-if-missing), AutoModSync.Client,
// and the apply helper. No server address, token, or additional network port is used.
// Runtime mod synchronization happens later through Valheim's existing ZRpc link.

#define WIN32_LEAN_AND_MEAN

typedef unsigned char  BYTE;
typedef unsigned short WORD;
typedef unsigned short WCHAR;
typedef unsigned int   UINT;
typedef unsigned long  DWORD;
typedef long           LONG;
typedef long long      LONGLONG;
typedef unsigned long long ULONGLONG;
typedef unsigned long long ULONG_PTR;
typedef unsigned long long SIZE_T;
typedef long LONG_PTR;
typedef unsigned long long DWORD_PTR;
typedef int BOOL;
typedef void* HANDLE;
typedef void* HMODULE;
typedef void* HINSTANCE;
typedef void* HWND;
typedef void* HINTERNET;
typedef void* HKEY;
typedef void* LPVOID;
typedef const void* LPCVOID;
typedef char CHAR;
typedef const char* LPCSTR;
typedef char* LPSTR;
typedef WCHAR* LPWSTR;
typedef const WCHAR* LPCWSTR;
typedef DWORD* LPDWORD;
typedef UINT* PUINT;
typedef BYTE* PBYTE;
typedef ULONG_PTR WPARAM;
typedef LONG_PTR LPARAM;
typedef LONG_PTR LRESULT;
typedef LONG NTSTATUS;
typedef void* FARPROC;

#define NULL ((void*)0)
#define TRUE 1
#define FALSE 0
#define WINAPI __stdcall
#define APIENTRY WINAPI
#define CALLBACK __stdcall
#define MAX_PATH_W 1024
#define DLL_PROCESS_ATTACH 1
#define INVALID_HANDLE_VALUE ((HANDLE)(LONG_PTR)-1)
#define GENERIC_READ 0x80000000UL
#define GENERIC_WRITE 0x40000000UL
#define FILE_SHARE_READ 0x00000001UL
#define OPEN_EXISTING 3
#define CREATE_ALWAYS 2
#define FILE_ATTRIBUTE_NORMAL 0x00000080UL
#define INVALID_FILE_ATTRIBUTES 0xFFFFFFFFUL
#define MOVEFILE_REPLACE_EXISTING 0x00000001UL
#define MOVEFILE_WRITE_THROUGH 0x00000008UL
#define HEAP_ZERO_MEMORY 0x00000008UL
#define KEY_QUERY_VALUE 0x0001
#define KEY_SET_VALUE 0x0002
#define KEY_CREATE_SUB_KEY 0x0004
#define REG_OPTION_NON_VOLATILE 0
#define REG_SZ 1
#define REG_DWORD 4
#define ERROR_SUCCESS 0
#define HKEY_CURRENT_USER ((HKEY)(ULONG_PTR)0x80000001UL)
#define CREATE_UNICODE_ENVIRONMENT 0x00000400UL
#define CREATE_NO_WINDOW 0x08000000UL
#define MB_OK 0x00000000UL
#define MB_ICONERROR 0x00000010UL
#define MB_ICONINFORMATION 0x00000040UL
#define MB_RETRYCANCEL 0x00000005UL
#define MB_ICONWARNING 0x00000030UL
#define IDRETRY 4
#define IDCANCEL 2
#define INTERNET_OPEN_TYPE_PRECONFIG 0
#define INTERNET_SERVICE_HTTP 3
#define INTERNET_FLAG_RELOAD 0x80000000UL
#define INTERNET_FLAG_NO_CACHE_WRITE 0x04000000UL
#define INTERNET_FLAG_PRAGMA_NOCACHE 0x00000100UL
#define HTTP_QUERY_STATUS_CODE 19
#define HTTP_QUERY_FLAG_NUMBER 0x20000000UL
#define BCRYPT_ALG_HANDLE_HMAC_FLAG 0x00000008UL
#define BCRYPT_USE_SYSTEM_PREFERRED_RNG 0x00000002UL
#define CREDUI_FLAGS_GENERIC_CREDENTIALS 0x00040000UL
#define CREDUI_FLAGS_DO_NOT_PERSIST 0x00000002UL
#define CREDUI_FLAGS_ALWAYS_SHOW_UI 0x00000080UL
#define NO_ERROR 0
#define CP_UTF8 65001

// Basic structures
typedef struct _FILETIME { DWORD dwLowDateTime; DWORD dwHighDateTime; } FILETIME;
typedef struct _LARGE_INTEGER { LONGLONG QuadPart; } LARGE_INTEGER;
typedef struct _SECURITY_ATTRIBUTES { DWORD nLength; LPVOID lpSecurityDescriptor; BOOL bInheritHandle; } SECURITY_ATTRIBUTES;
typedef struct _STARTUPINFOW {
    DWORD cb; LPWSTR lpReserved; LPWSTR lpDesktop; LPWSTR lpTitle; DWORD dwX; DWORD dwY; DWORD dwXSize; DWORD dwYSize;
    DWORD dwXCountChars; DWORD dwYCountChars; DWORD dwFillAttribute; DWORD dwFlags; WORD wShowWindow; WORD cbReserved2;
    BYTE* lpReserved2; HANDLE hStdInput; HANDLE hStdOutput; HANDLE hStdError;
} STARTUPINFOW;
typedef struct _PROCESS_INFORMATION { HANDLE hProcess; HANDLE hThread; DWORD dwProcessId; DWORD dwThreadId; } PROCESS_INFORMATION;
typedef struct _LIST_ENTRY { struct _LIST_ENTRY* Flink; struct _LIST_ENTRY* Blink; } LIST_ENTRY;
typedef struct _UNICODE_STRING { WORD Length; WORD MaximumLength; WCHAR* Buffer; } UNICODE_STRING;
typedef struct _PEB_LDR_DATA_X { DWORD Length; BYTE Initialized; BYTE pad1[3]; HANDLE SsHandle; LIST_ENTRY InLoadOrderModuleList; } PEB_LDR_DATA_X;
typedef struct _LDR_DATA_TABLE_ENTRY_X {
    LIST_ENTRY InLoadOrderLinks; LIST_ENTRY InMemoryOrderLinks; LIST_ENTRY InInitializationOrderLinks;
    LPVOID DllBase; LPVOID EntryPoint; DWORD SizeOfImage; DWORD pad2; UNICODE_STRING FullDllName; UNICODE_STRING BaseDllName;
} LDR_DATA_TABLE_ENTRY_X;
typedef struct _PEB_X { BYTE Reserved1[0x18]; PEB_LDR_DATA_X* Ldr; } PEB_X;
typedef struct _CREDUI_INFOW { DWORD cbSize; HWND hwndParent; LPCWSTR pszMessageText; LPCWSTR pszCaptionText; void* hbmBanner; } CREDUI_INFOW;

// Kernel32 API types
typedef HMODULE (WINAPI *PFN_LoadLibraryW)(LPCWSTR);
typedef FARPROC (WINAPI *PFN_GetProcAddress)(HMODULE,LPCSTR);
typedef DWORD (WINAPI *PFN_GetModuleFileNameW)(HMODULE,LPWSTR,DWORD);
typedef LPWSTR (WINAPI *PFN_GetCommandLineW)(void);
typedef DWORD (WINAPI *PFN_GetCurrentDirectoryW)(DWORD,LPWSTR);
typedef BOOL (WINAPI *PFN_SetCurrentDirectoryW)(LPCWSTR);
typedef BOOL (WINAPI *PFN_CreateProcessW)(LPCWSTR,LPWSTR,SECURITY_ATTRIBUTES*,SECURITY_ATTRIBUTES*,BOOL,DWORD,LPVOID,LPCWSTR,STARTUPINFOW*,PROCESS_INFORMATION*);
typedef void (WINAPI *PFN_ExitProcess)(UINT);
typedef DWORD (WINAPI *PFN_GetEnvironmentVariableW)(LPCWSTR,LPWSTR,DWORD);
typedef BOOL (WINAPI *PFN_SetEnvironmentVariableW)(LPCWSTR,LPCWSTR);
typedef HANDLE (WINAPI *PFN_CreateThread)(SECURITY_ATTRIBUTES*,SIZE_T,DWORD (WINAPI*)(LPVOID),LPVOID,DWORD,LPDWORD);
typedef void (WINAPI *PFN_Sleep)(DWORD);
typedef DWORD (WINAPI *PFN_GetSystemDirectoryW)(LPWSTR,UINT);
typedef HANDLE (WINAPI *PFN_CreateFileW)(LPCWSTR,DWORD,DWORD,SECURITY_ATTRIBUTES*,DWORD,DWORD,HANDLE);
typedef BOOL (WINAPI *PFN_ReadFile)(HANDLE,LPVOID,DWORD,LPDWORD,LPVOID);
typedef BOOL (WINAPI *PFN_WriteFile)(HANDLE,LPCVOID,DWORD,LPDWORD,LPVOID);
typedef BOOL (WINAPI *PFN_CloseHandle)(HANDLE);
typedef BOOL (WINAPI *PFN_GetFileSizeEx)(HANDLE,LARGE_INTEGER*);
typedef DWORD (WINAPI *PFN_GetFileAttributesW)(LPCWSTR);
typedef BOOL (WINAPI *PFN_CreateDirectoryW)(LPCWSTR,SECURITY_ATTRIBUTES*);
typedef BOOL (WINAPI *PFN_MoveFileExW)(LPCWSTR,LPCWSTR,DWORD);
typedef BOOL (WINAPI *PFN_DeleteFileW)(LPCWSTR);
typedef HANDLE (WINAPI *PFN_GetProcessHeap)(void);
typedef LPVOID (WINAPI *PFN_HeapAlloc)(HANDLE,DWORD,SIZE_T);
typedef LPVOID (WINAPI *PFN_HeapReAlloc)(HANDLE,DWORD,LPVOID,SIZE_T);
typedef BOOL (WINAPI *PFN_HeapFree)(HANDLE,DWORD,LPVOID);
typedef void (WINAPI *PFN_GetSystemTimeAsFileTime)(FILETIME*);
typedef BOOL (WINAPI *PFN_DisableThreadLibraryCalls)(HMODULE);
typedef int (WINAPI *PFN_WideCharToMultiByte)(UINT,DWORD,LPCWSTR,int,LPSTR,int,LPCSTR,BOOL*);
typedef int (WINAPI *PFN_MultiByteToWideChar)(UINT,DWORD,LPCSTR,int,LPWSTR,int);

// Registry API
typedef LONG (WINAPI *PFN_RegCreateKeyExW)(HKEY,LPCWSTR,DWORD,LPWSTR,DWORD,DWORD,SECURITY_ATTRIBUTES*,HKEY*,LPDWORD);
typedef LONG (WINAPI *PFN_RegOpenKeyExW)(HKEY,LPCWSTR,DWORD,DWORD,HKEY*);
typedef LONG (WINAPI *PFN_RegQueryValueExW)(HKEY,LPCWSTR,LPDWORD,LPDWORD,PBYTE,LPDWORD);
typedef LONG (WINAPI *PFN_RegSetValueExW)(HKEY,LPCWSTR,DWORD,DWORD,const BYTE*,DWORD);
typedef LONG (WINAPI *PFN_RegCloseKey)(HKEY);

// User/CredUI
typedef int (WINAPI *PFN_MessageBoxW)(HWND,LPCWSTR,LPCWSTR,UINT);
typedef DWORD (WINAPI *PFN_CredUIPromptForCredentialsW)(CREDUI_INFOW*,LPCWSTR,void*,DWORD,LPWSTR,DWORD,LPWSTR,DWORD,BOOL*,DWORD);

// WinINet
typedef HINTERNET (WINAPI *PFN_InternetOpenW)(LPCWSTR,DWORD,LPCWSTR,LPCWSTR,DWORD);
typedef HINTERNET (WINAPI *PFN_InternetConnectW)(HINTERNET,LPCWSTR,WORD,LPCWSTR,LPCWSTR,DWORD,DWORD,DWORD_PTR);
typedef HINTERNET (WINAPI *PFN_HttpOpenRequestW)(HINTERNET,LPCWSTR,LPCWSTR,LPCWSTR,LPCWSTR,LPCWSTR*,DWORD,DWORD_PTR);
typedef BOOL (WINAPI *PFN_HttpSendRequestW)(HINTERNET,LPCWSTR,DWORD,LPVOID,DWORD);
typedef BOOL (WINAPI *PFN_HttpQueryInfoW)(HINTERNET,DWORD,LPVOID,LPDWORD,LPDWORD);
typedef BOOL (WINAPI *PFN_InternetReadFile)(HINTERNET,LPVOID,DWORD,LPDWORD);
typedef BOOL (WINAPI *PFN_InternetCloseHandle)(HINTERNET);

// BCrypt
typedef void* BCRYPT_ALG_HANDLE;
typedef NTSTATUS (WINAPI *PFN_BCryptOpenAlgorithmProvider)(BCRYPT_ALG_HANDLE*,LPCWSTR,LPCWSTR,DWORD);
typedef NTSTATUS (WINAPI *PFN_BCryptCloseAlgorithmProvider)(BCRYPT_ALG_HANDLE,DWORD);
typedef NTSTATUS (WINAPI *PFN_BCryptHash)(BCRYPT_ALG_HANDLE,PBYTE,DWORD,PBYTE,DWORD,PBYTE,DWORD);
typedef NTSTATUS (WINAPI *PFN_BCryptGenRandom)(BCRYPT_ALG_HANDLE,PBYTE,DWORD,DWORD);

// version.dll export signatures
typedef BOOL (WINAPI *PFN_GetFileVersionInfoA)(LPCSTR,DWORD,DWORD,LPVOID);
typedef BOOL (WINAPI *PFN_GetFileVersionInfoW)(LPCWSTR,DWORD,DWORD,LPVOID);
typedef BOOL (WINAPI *PFN_GetFileVersionInfoExA)(DWORD,LPCSTR,DWORD,DWORD,LPVOID);
typedef BOOL (WINAPI *PFN_GetFileVersionInfoExW)(DWORD,LPCWSTR,DWORD,DWORD,LPVOID);
typedef DWORD (WINAPI *PFN_GetFileVersionInfoSizeA)(LPCSTR,LPDWORD);
typedef DWORD (WINAPI *PFN_GetFileVersionInfoSizeW)(LPCWSTR,LPDWORD);
typedef DWORD (WINAPI *PFN_GetFileVersionInfoSizeExA)(DWORD,LPCSTR,LPDWORD);
typedef DWORD (WINAPI *PFN_GetFileVersionInfoSizeExW)(DWORD,LPCWSTR,LPDWORD);
typedef BOOL (WINAPI *PFN_GetFileVersionInfoByHandle)(DWORD,HANDLE,LPVOID*,LPDWORD);
typedef DWORD (WINAPI *PFN_VerFindFileA)(DWORD,LPCSTR,LPCSTR,LPCSTR,LPSTR,PUINT,LPSTR,PUINT);
typedef DWORD (WINAPI *PFN_VerFindFileW)(DWORD,LPCWSTR,LPCWSTR,LPCWSTR,LPWSTR,PUINT,LPWSTR,PUINT);
typedef DWORD (WINAPI *PFN_VerInstallFileA)(DWORD,LPCSTR,LPCSTR,LPCSTR,LPCSTR,LPCSTR,LPSTR,PUINT);
typedef DWORD (WINAPI *PFN_VerInstallFileW)(DWORD,LPCWSTR,LPCWSTR,LPCWSTR,LPCWSTR,LPCWSTR,LPWSTR,PUINT);
typedef DWORD (WINAPI *PFN_VerLanguageNameA)(DWORD,LPSTR,DWORD);
typedef DWORD (WINAPI *PFN_VerLanguageNameW)(DWORD,LPWSTR,DWORD);
typedef BOOL (WINAPI *PFN_VerQueryValueA)(LPCVOID,LPCSTR,LPVOID*,PUINT);
typedef BOOL (WINAPI *PFN_VerQueryValueW)(LPCVOID,LPCWSTR,LPVOID*,PUINT);

static HMODULE g_self = NULL;
static PFN_LoadLibraryW pLoadLibraryW; static PFN_GetProcAddress pGetProcAddress;
static PFN_GetModuleFileNameW pGetModuleFileNameW; static PFN_GetCommandLineW pGetCommandLineW;
static PFN_GetCurrentDirectoryW pGetCurrentDirectoryW; static PFN_SetCurrentDirectoryW pSetCurrentDirectoryW;
static PFN_CreateProcessW pCreateProcessW; static PFN_ExitProcess pExitProcess;
static PFN_GetEnvironmentVariableW pGetEnvironmentVariableW; static PFN_SetEnvironmentVariableW pSetEnvironmentVariableW;
static PFN_CreateThread pCreateThread; static PFN_Sleep pSleep; static PFN_GetSystemDirectoryW pGetSystemDirectoryW;
static PFN_CreateFileW pCreateFileW; static PFN_ReadFile pReadFile; static PFN_WriteFile pWriteFile; static PFN_CloseHandle pCloseHandle;
static PFN_GetFileSizeEx pGetFileSizeEx; static PFN_GetFileAttributesW pGetFileAttributesW; static PFN_CreateDirectoryW pCreateDirectoryW;
static PFN_MoveFileExW pMoveFileExW; static PFN_DeleteFileW pDeleteFileW; static PFN_GetProcessHeap pGetProcessHeap;
static PFN_HeapAlloc pHeapAlloc; static PFN_HeapReAlloc pHeapReAlloc; static PFN_HeapFree pHeapFree;
static PFN_GetSystemTimeAsFileTime pGetSystemTimeAsFileTime; static PFN_DisableThreadLibraryCalls pDisableThreadLibraryCalls;
static PFN_WideCharToMultiByte pWideCharToMultiByte; static PFN_MultiByteToWideChar pMultiByteToWideChar;

static PFN_RegCreateKeyExW pRegCreateKeyExW; static PFN_RegOpenKeyExW pRegOpenKeyExW; static PFN_RegQueryValueExW pRegQueryValueExW;
static PFN_RegSetValueExW pRegSetValueExW; static PFN_RegCloseKey pRegCloseKey;
static PFN_MessageBoxW pMessageBoxW; static PFN_CredUIPromptForCredentialsW pCredUIPromptForCredentialsW;
static PFN_InternetOpenW pInternetOpenW; static PFN_InternetConnectW pInternetConnectW; static PFN_HttpOpenRequestW pHttpOpenRequestW;
static PFN_HttpSendRequestW pHttpSendRequestW; static PFN_HttpQueryInfoW pHttpQueryInfoW; static PFN_InternetReadFile pInternetReadFile; static PFN_InternetCloseHandle pInternetCloseHandle;
static PFN_BCryptOpenAlgorithmProvider pBCryptOpenAlgorithmProvider; static PFN_BCryptCloseAlgorithmProvider pBCryptCloseAlgorithmProvider;
static PFN_BCryptHash pBCryptHash; static PFN_BCryptGenRandom pBCryptGenRandom;

static PFN_GetFileVersionInfoA vGetFileVersionInfoA; static PFN_GetFileVersionInfoW vGetFileVersionInfoW;
static PFN_GetFileVersionInfoExA vGetFileVersionInfoExA; static PFN_GetFileVersionInfoExW vGetFileVersionInfoExW;
static PFN_GetFileVersionInfoSizeA vGetFileVersionInfoSizeA; static PFN_GetFileVersionInfoSizeW vGetFileVersionInfoSizeW;
static PFN_GetFileVersionInfoSizeExA vGetFileVersionInfoSizeExA; static PFN_GetFileVersionInfoSizeExW vGetFileVersionInfoSizeExW; static PFN_GetFileVersionInfoByHandle vGetFileVersionInfoByHandle;
static PFN_VerFindFileA vVerFindFileA; static PFN_VerFindFileW vVerFindFileW; static PFN_VerInstallFileA vVerInstallFileA; static PFN_VerInstallFileW vVerInstallFileW;
static PFN_VerLanguageNameA vVerLanguageNameA; static PFN_VerLanguageNameW vVerLanguageNameW; static PFN_VerQueryValueA vVerQueryValueA; static PFN_VerQueryValueW vVerQueryValueW;
static HMODULE g_real_version = NULL;

static const WCHAR W_KERNEL32[] = {'K','E','R','N','E','L','3','2','.','D','L','L',0};
static const WCHAR W_SOFTWARE_KEY[] = {'S','o','f','t','w','a','r','e','\\','V','a','l','h','e','i','m','A','u','t','o','M','o','d','S','y','n','c',0};
static const WCHAR W_READY[] = {'A','U','T','O','M','O','D','S','Y','N','C','_','R','E','A','D','Y',0};
static const WCHAR W_VALHEIM[] = {'v','a','l','h','e','i','m','.','e','x','e',0};
static const WCHAR W_BEPINEX_DLL[] = {'B','e','p','I','n','E','x','\\','c','o','r','e','\\','B','e','p','I','n','E','x','.','d','l','l',0};

void* memcpy(void* d,const void* s,SIZE_T n){BYTE*dd=(BYTE*)d;const BYTE*ss=(const BYTE*)s;while(n--)*dd++=*ss++;return d;}
void* memset(void* d,int c,SIZE_T n){BYTE*dd=(BYTE*)d;while(n--)*dd++=(BYTE)c;return d;}
static void* mcpy(void* d,const void* s,SIZE_T n){BYTE*dd=(BYTE*)d;const BYTE*ss=(const BYTE*)s;while(n--)*dd++=*ss++;return d;}
static void* mset(void* d,int c,SIZE_T n){BYTE*dd=(BYTE*)d;while(n--)*dd++=(BYTE)c;return d;}
static SIZE_T slen(const char*s){SIZE_T n=0;if(!s)return 0;while(s[n])n++;return n;}
static SIZE_T wslen(const WCHAR*s){SIZE_T n=0;if(!s)return 0;while(s[n])n++;return n;}
static int acmp(const char*a,const char*b){while(*a&&*b&&*a==*b){a++;b++;}return (unsigned char)*a-(unsigned char)*b;}
static WCHAR wlower(WCHAR c){if(c>='A'&&c<='Z')return c+32;return c;}
static int wicmp(const WCHAR*a,const WCHAR*b){while(*a&&*b&&wlower(*a)==wlower(*b)){a++;b++;}return (int)wlower(*a)-(int)wlower(*b);}
static void wcopy(WCHAR*d,const WCHAR*s,SIZE_T cap){SIZE_T i=0;if(!cap)return;for(;i+1<cap&&s&&s[i];i++)d[i]=s[i];d[i]=0;}
static void wcat(WCHAR*d,const WCHAR*s,SIZE_T cap){SIZE_T n=wslen(d),i=0;if(n>=cap)return;for(;n+i+1<cap&&s&&s[i];i++)d[n+i]=s[i];d[n+i]=0;}
static void acopy(char*d,const char*s,SIZE_T cap){SIZE_T i=0;if(!cap)return;for(;i+1<cap&&s&&s[i];i++)d[i]=s[i];d[i]=0;}
static void acat(char*d,const char*s,SIZE_T cap){SIZE_T n=slen(d),i=0;for(;n+i+1<cap&&s&&s[i];i++)d[n+i]=s[i];d[n+i]=0;}
static void u64toa(ULONGLONG v,char*out,SIZE_T cap){char t[32];SIZE_T n=0,i;if(!cap)return;if(v==0){out[0]='0';if(cap>1)out[1]=0;return;}while(v&&n<31){t[n++]=(char)('0'+(v%10));v/=10;}i=0;while(n&&i+1<cap)out[i++]=t[--n];out[i]=0;}
static int hexval(char c){if(c>='0'&&c<='9')return c-'0';if(c>='a'&&c<='f')return c-'a'+10;if(c>='A'&&c<='F')return c-'A'+10;return -1;}
static void bytes_to_hex(const BYTE*in,DWORD n,char*out){static const char h[]="0123456789abcdef";DWORD i;for(i=0;i<n;i++){out[i*2]=h[in[i]>>4];out[i*2+1]=h[in[i]&15];}out[n*2]=0;}
static BOOL hex_to_bytes(const char*in,BYTE*out,DWORD n){DWORD i;for(i=0;i<n;i++){int a=hexval(in[i*2]),b=hexval(in[i*2+1]);if(a<0||b<0)return FALSE;out[i]=(BYTE)((a<<4)|b);}return TRUE;}

static PEB_X* get_peb(void){PEB_X* p; __asm__("movq %%gs:0x60,%0":"=r"(p)); return p;}
static BOOL module_name_eq(const UNICODE_STRING* us,const WCHAR* name){
    SIZE_T i,n,m;if(!us||!us->Buffer||!name)return FALSE;n=(SIZE_T)(us->Length/2);m=wslen(name);if(n!=m)return FALSE;
    for(i=0;i<n;i++)if(wlower(us->Buffer[i])!=wlower(name[i]))return FALSE;return TRUE;
}
static HMODULE find_module(const WCHAR* name){
    PEB_X* peb=get_peb(); if(!peb||!peb->Ldr)return NULL; LIST_ENTRY* head=&peb->Ldr->InLoadOrderModuleList; LIST_ENTRY* e=head->Flink;
    while(e&&e!=head){LDR_DATA_TABLE_ENTRY_X* ent=(LDR_DATA_TABLE_ENTRY_X*)e; if(module_name_eq(&ent->BaseDllName,name))return (HMODULE)ent->DllBase; e=e->Flink;} return NULL;
}
static DWORD parse_u32_ascii(const char*s){DWORD v=0;if(!s||!*s)return 0;while(*s){if(*s<'0'||*s>'9')return 0;v=v*10+(DWORD)(*s-'0');s++;}return v;}
static FARPROC resolve_export_depth(HMODULE mod,const char*name,int depth);
static FARPROC resolve_export_ordinal_depth(HMODULE mod,DWORD ordinal,int depth){
    BYTE*b;DWORD pe,erva,esize,base,nf,frva,*funcs;BYTE*nt,*ed;if(!mod||depth>8)return NULL;b=(BYTE*)mod;pe=*(DWORD*)(b+0x3c);nt=b+pe;if(*(WORD*)(nt+24)!=0x20b)return NULL;
    erva=*(DWORD*)(nt+24+112);esize=*(DWORD*)(nt+24+116);if(!erva)return NULL;ed=b+erva;base=*(DWORD*)(ed+16);nf=*(DWORD*)(ed+20);if(ordinal<base||ordinal-base>=nf)return NULL;
    funcs=(DWORD*)(b+*(DWORD*)(ed+28));frva=funcs[ordinal-base];if(frva>=erva&&frva<erva+esize){char*fwd=(char*)(b+frva),dll[128],sym[128];SIZE_T i=0,j=0;WCHAR wdll[160];HMODULE target;
        while(fwd[i]&&fwd[i]!='.'&&i+1<sizeof(dll)){dll[i]=fwd[i];i++;}dll[i]=0;if(fwd[i]!='.')return NULL;i++;
        while(fwd[i]&&j+1<sizeof(sym))sym[j++]=fwd[i++];sym[j]=0;if(!dll[0]||!sym[0])return NULL;
        j=0;while(dll[j]&&j+5<160){wdll[j]=(WCHAR)dll[j];j++;}if(j<4||!(wlower(wdll[j-4])=='.'&&wlower(wdll[j-3])=='d'&&wlower(wdll[j-2])=='l'&&wlower(wdll[j-1])=='l')){wdll[j++]='.';wdll[j++]='D';wdll[j++]='L';wdll[j++]='L';}wdll[j]=0;
        target=find_module(wdll);if(!target)return NULL;if(sym[0]=='#')return resolve_export_ordinal_depth(target,parse_u32_ascii(sym+1),depth+1);return resolve_export_depth(target,sym,depth+1);
    }return (FARPROC)(b+frva);
}
static FARPROC resolve_export_depth(HMODULE mod,const char*name,int depth){
    BYTE*b;DWORD pe,erva,esize,nf,nn,*funcs,*names;WORD*ords;BYTE*nt,*ed;DWORD i;if(!mod||!name||depth>8)return NULL;b=(BYTE*)mod;pe=*(DWORD*)(b+0x3c);nt=b+pe;if(*(WORD*)(nt+24)!=0x20b)return NULL;
    erva=*(DWORD*)(nt+24+112);esize=*(DWORD*)(nt+24+116);if(!erva)return NULL;ed=b+erva;nf=*(DWORD*)(ed+20);nn=*(DWORD*)(ed+24);funcs=(DWORD*)(b+*(DWORD*)(ed+28));names=(DWORD*)(b+*(DWORD*)(ed+32));ords=(WORD*)(b+*(DWORD*)(ed+36));
    for(i=0;i<nn;i++){char*n=(char*)(b+names[i]);if(acmp(n,name)==0){WORD o=ords[i];DWORD frva;if(o>=nf)return NULL;frva=funcs[o];if(frva>=erva&&frva<erva+esize){char*fwd=(char*)(b+frva),dll[128],sym[128];SIZE_T a=0,j=0;WCHAR wdll[160];HMODULE target;
            while(fwd[a]&&fwd[a]!='.'&&a+1<sizeof(dll)){dll[a]=fwd[a];a++;}dll[a]=0;if(fwd[a]!='.')return NULL;a++;while(fwd[a]&&j+1<sizeof(sym))sym[j++]=fwd[a++];sym[j]=0;if(!dll[0]||!sym[0])return NULL;
            j=0;while(dll[j]&&j+5<160){wdll[j]=(WCHAR)dll[j];j++;}if(j<4||!(wlower(wdll[j-4])=='.'&&wlower(wdll[j-3])=='d'&&wlower(wdll[j-2])=='l'&&wlower(wdll[j-1])=='l')){wdll[j++]='.';wdll[j++]='D';wdll[j++]='L';wdll[j++]='L';}wdll[j]=0;
            target=find_module(wdll);if(!target)return NULL;if(sym[0]=='#')return resolve_export_ordinal_depth(target,parse_u32_ascii(sym+1),depth+1);return resolve_export_depth(target,sym,depth+1);
        }return (FARPROC)(b+frva);}}
    return NULL;
}
static FARPROC resolve_export(HMODULE mod,const char*name){return resolve_export_depth(mod,name,0);}

static void init_kernel(void){
    HMODULE k=find_module(W_KERNEL32); if(!k)return;
#define RESK(name) p##name=(PFN_##name)resolve_export(k,#name)
    RESK(LoadLibraryW); RESK(GetProcAddress); RESK(GetModuleFileNameW); RESK(GetCommandLineW); RESK(GetCurrentDirectoryW); RESK(SetCurrentDirectoryW);
    RESK(CreateProcessW); RESK(ExitProcess); RESK(GetEnvironmentVariableW); RESK(SetEnvironmentVariableW); RESK(CreateThread); RESK(Sleep); RESK(GetSystemDirectoryW);
    RESK(CreateFileW); RESK(ReadFile); RESK(WriteFile); RESK(CloseHandle); RESK(GetFileSizeEx); RESK(GetFileAttributesW); RESK(CreateDirectoryW); RESK(MoveFileExW); RESK(DeleteFileW);
    RESK(GetProcessHeap); RESK(HeapAlloc); RESK(HeapReAlloc); RESK(HeapFree); RESK(GetSystemTimeAsFileTime); RESK(DisableThreadLibraryCalls); RESK(WideCharToMultiByte); RESK(MultiByteToWideChar);
#undef RESK
}
static HMODULE load_system_dll(const WCHAR*name){WCHAR p[MAX_PATH_W];if(!pGetSystemDirectoryW||!pLoadLibraryW)return NULL;p[0]=0;pGetSystemDirectoryW(p,MAX_PATH_W);wcat(p,(const WCHAR[]){'\\',0},MAX_PATH_W);wcat(p,name,MAX_PATH_W);return pLoadLibraryW(p);}
static void init_aux(void){
    HMODULE a=load_system_dll((const WCHAR[]){'a','d','v','a','p','i','3','2','.','d','l','l',0});
    HMODULE u=load_system_dll((const WCHAR[]){'u','s','e','r','3','2','.','d','l','l',0});
    HMODULE b=load_system_dll((const WCHAR[]){'b','c','r','y','p','t','.','d','l','l',0});
#define RES(mod,var,type,name) var=(type)(pGetProcAddress?pGetProcAddress(mod,name):NULL)
    if(a){RES(a,pRegCreateKeyExW,PFN_RegCreateKeyExW,"RegCreateKeyExW");RES(a,pRegOpenKeyExW,PFN_RegOpenKeyExW,"RegOpenKeyExW");RES(a,pRegQueryValueExW,PFN_RegQueryValueExW,"RegQueryValueExW");RES(a,pRegSetValueExW,PFN_RegSetValueExW,"RegSetValueExW");RES(a,pRegCloseKey,PFN_RegCloseKey,"RegCloseKey");}
    if(u)RES(u,pMessageBoxW,PFN_MessageBoxW,"MessageBoxW");
    if(b){RES(b,pBCryptOpenAlgorithmProvider,PFN_BCryptOpenAlgorithmProvider,"BCryptOpenAlgorithmProvider");RES(b,pBCryptCloseAlgorithmProvider,PFN_BCryptCloseAlgorithmProvider,"BCryptCloseAlgorithmProvider");RES(b,pBCryptHash,PFN_BCryptHash,"BCryptHash");}
#undef RES
}
static void init_real_version(void){
    HMODULE v;if(g_real_version||!pGetProcAddress)return;v=load_system_dll((const WCHAR[]){'v','e','r','s','i','o','n','.','d','l','l',0}); if(!v)return;g_real_version=v;
#define RV(var,type,name) var=(type)pGetProcAddress(v,name)
    RV(vGetFileVersionInfoA,PFN_GetFileVersionInfoA,"GetFileVersionInfoA"); RV(vGetFileVersionInfoW,PFN_GetFileVersionInfoW,"GetFileVersionInfoW");
    RV(vGetFileVersionInfoExA,PFN_GetFileVersionInfoExA,"GetFileVersionInfoExA"); RV(vGetFileVersionInfoExW,PFN_GetFileVersionInfoExW,"GetFileVersionInfoExW");
    RV(vGetFileVersionInfoSizeA,PFN_GetFileVersionInfoSizeA,"GetFileVersionInfoSizeA"); RV(vGetFileVersionInfoSizeW,PFN_GetFileVersionInfoSizeW,"GetFileVersionInfoSizeW");
    RV(vGetFileVersionInfoSizeExA,PFN_GetFileVersionInfoSizeExA,"GetFileVersionInfoSizeExA"); RV(vGetFileVersionInfoSizeExW,PFN_GetFileVersionInfoSizeExW,"GetFileVersionInfoSizeExW"); RV(vGetFileVersionInfoByHandle,PFN_GetFileVersionInfoByHandle,"GetFileVersionInfoByHandle");
    RV(vVerFindFileA,PFN_VerFindFileA,"VerFindFileA"); RV(vVerFindFileW,PFN_VerFindFileW,"VerFindFileW"); RV(vVerInstallFileA,PFN_VerInstallFileA,"VerInstallFileA"); RV(vVerInstallFileW,PFN_VerInstallFileW,"VerInstallFileW");
    RV(vVerLanguageNameA,PFN_VerLanguageNameA,"VerLanguageNameA"); RV(vVerLanguageNameW,PFN_VerLanguageNameW,"VerLanguageNameW"); RV(vVerQueryValueA,PFN_VerQueryValueA,"VerQueryValueA"); RV(vVerQueryValueW,PFN_VerQueryValueW,"VerQueryValueW");
#undef RV
}
static void ensure_real_version(void){if(!g_real_version)init_real_version();}

static BOOL file_exists(LPCWSTR p){return pGetFileAttributesW&&pGetFileAttributesW(p)!=INVALID_FILE_ATTRIBUTES;}
static void strip_filename(WCHAR*p){SIZE_T n=wslen(p);while(n){if(p[n-1]=='\\'||p[n-1]=='/'){p[n-1]=0;return;}n--;}}
static void get_game_root(WCHAR*out,SIZE_T cap){out[0]=0;if(pGetModuleFileNameW)pGetModuleFileNameW(g_self,out,(DWORD)cap);strip_filename(out);}
static BOOL get_reg_string(LPCWSTR name,WCHAR*out,DWORD cap){HKEY k;DWORD t=0,cb=cap*2;if(!pRegOpenKeyExW||pRegOpenKeyExW(HKEY_CURRENT_USER,W_SOFTWARE_KEY,0,KEY_QUERY_VALUE,&k)!=ERROR_SUCCESS)return FALSE;LONG r=pRegQueryValueExW(k,name,NULL,&t,(PBYTE)out,&cb);pRegCloseKey(k);if(r!=ERROR_SUCCESS||t!=REG_SZ)return FALSE;out[cap-1]=0;return TRUE;}
static BOOL set_reg_string(LPCWSTR name,LPCWSTR val){HKEY k;DWORD disp;if(!pRegCreateKeyExW||pRegCreateKeyExW(HKEY_CURRENT_USER,W_SOFTWARE_KEY,0,NULL,REG_OPTION_NON_VOLATILE,KEY_QUERY_VALUE|KEY_SET_VALUE,NULL,&k,&disp)!=ERROR_SUCCESS)return FALSE;DWORD cb=(DWORD)((wslen(val)+1)*2);LONG r=pRegSetValueExW(k,name,0,REG_SZ,(const BYTE*)val,cb);pRegCloseKey(k);return r==ERROR_SUCCESS;}
static BOOL sha256(const BYTE*data,DWORD len,BYTE out[32]){
    if(!pBCryptOpenAlgorithmProvider||!pBCryptHash)return FALSE; BCRYPT_ALG_HANDLE h=NULL; const WCHAR alg[]={'S','H','A','2','5','6',0};
    if(pBCryptOpenAlgorithmProvider(&h,alg,NULL,0)<0)return FALSE; NTSTATUS s=pBCryptHash(h,NULL,0,(PBYTE)data,len,out,32);pBCryptCloseAlgorithmProvider(h,0);return s>=0;
}
static BOOL file_sha256(LPCWSTR path,BYTE out[32]){
    HANDLE f=pCreateFileW(path,GENERIC_READ,FILE_SHARE_READ,NULL,OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL,NULL); if(f==INVALID_HANDLE_VALUE)return FALSE;
    LARGE_INTEGER sz;if(!pGetFileSizeEx(f,&sz)||sz.QuadPart<0||sz.QuadPart>268435456LL){pCloseHandle(f);return FALSE;}DWORD n=(DWORD)sz.QuadPart;BYTE*buf=(BYTE*)pHeapAlloc(pGetProcessHeap(),0,n?n:1);if(!buf){pCloseHandle(f);return FALSE;}DWORD got=0,total=0;while(total<n){if(!pReadFile(f,buf+total,n-total,&got,NULL)||!got)break;total+=got;}pCloseHandle(f);BOOL ok=(total==n)&&sha256(buf,n,out);pHeapFree(pGetProcessHeap(),0,buf);return ok;
}
static void wide_to_utf8(LPCWSTR w,char*out,int cap){if(!pWideCharToMultiByte){out[0]=0;return;}pWideCharToMultiByte(CP_UTF8,0,w,-1,out,cap,NULL,NULL);out[cap-1]=0;}
static void utf8_to_wide(LPCSTR s,WCHAR*out,int cap){if(!pMultiByteToWideChar){out[0]=0;return;}pMultiByteToWideChar(CP_UTF8,0,s,-1,out,cap);out[cap-1]=0;}

static void ensure_parent_dirs(WCHAR*path){WCHAR tmp[MAX_PATH_W];wcopy(tmp,path,MAX_PATH_W);SIZE_T i=0,n=wslen(tmp);for(i=3;i<n;i++){if(tmp[i]=='\\'||tmp[i]=='/'){WCHAR c=tmp[i];tmp[i]=0;pCreateDirectoryW(tmp,NULL);tmp[i]=c;}}}
static BOOL write_atomic(LPCWSTR path,const BYTE*data,DWORD len){WCHAR tmp[MAX_PATH_W];wcopy(tmp,path,MAX_PATH_W);wcat(tmp,(const WCHAR[]){'.','a','m','s','t','m','p',0},MAX_PATH_W);ensure_parent_dirs(tmp);HANDLE f=pCreateFileW(tmp,GENERIC_WRITE,0,NULL,CREATE_ALWAYS,FILE_ATTRIBUTE_NORMAL,NULL);if(f==INVALID_HANDLE_VALUE)return FALSE;DWORD off=0,w=0;while(off<len){if(!pWriteFile(f,data+off,len-off,&w,NULL)||!w){pCloseHandle(f);pDeleteFileW(tmp);return FALSE;}off+=w;}pCloseHandle(f);if(!pMoveFileExW(tmp,path,MOVEFILE_REPLACE_EXISTING|MOVEFILE_WRITE_THROUGH)){pDeleteFileW(tmp);return FALSE;}return TRUE;}
static BOOL needs_file(LPCWSTR path,const char*shahex){if(!file_exists(path))return TRUE;BYTE h[32],e[32];if(!hex_to_bytes(shahex,e,32)||!file_sha256(path,h))return TRUE;DWORD i;for(i=0;i<32;i++)if(h[i]!=e[i])return TRUE;return FALSE;}
static ULONGLONG parse_u64_dec(const char*s){ULONGLONG v=0;if(!s||!*s)return 0;while(*s){if(*s<'0'||*s>'9')return 0;v=v*10+(ULONGLONG)(*s-'0');s++;}return v;}
static BOOL read_self_image(BYTE**out,DWORD*len){
    WCHAR self[MAX_PATH_W];LARGE_INTEGER sz;DWORD got=0,total=0;HANDLE f;BYTE*b;
    if(!pGetModuleFileNameW||!pCreateFileW||!pGetFileSizeEx)return FALSE;pGetModuleFileNameW(g_self,self,MAX_PATH_W);
    f=pCreateFileW(self,GENERIC_READ,FILE_SHARE_READ,NULL,OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL,NULL);if(f==INVALID_HANDLE_VALUE)return FALSE;
    if(!pGetFileSizeEx(f,&sz)||sz.QuadPart<24||sz.QuadPart>134217728LL){pCloseHandle(f);return FALSE;}
    *len=(DWORD)sz.QuadPart;b=(BYTE*)pHeapAlloc(pGetProcessHeap(),0,*len);if(!b){pCloseHandle(f);return FALSE;}
    while(total<*len){if(!pReadFile(f,b+total,*len-total,&got,NULL)||!got)break;total+=got;}pCloseHandle(f);
    if(total!=*len){pHeapFree(pGetProcessHeap(),0,b);return FALSE;}*out=b;return TRUE;
}
static BOOL payload_manifest(BYTE*image,DWORD len,BYTE**manifest,DWORD*mlen,ULONGLONG*manifestOff){
    static const char magic[8]={'A','M','S','2','P','K','G','1'};DWORD i;ULONGLONG off=0,ln=0;if(len<24)return FALSE;
    for(i=0;i<8;i++)if(image[len-24+i]!=(BYTE)magic[i])return FALSE;
    for(i=0;i<8;i++)off|=((ULONGLONG)image[len-16+i])<<(i*8);
    for(i=0;i<8;i++)ln|=((ULONGLONG)image[len-8+i])<<(i*8);
    if(off>len||ln>len||off+ln>(ULONGLONG)(len-24)||ln>0xFFFFFFFFULL)return FALSE;
    *manifest=image+(DWORD)off;*mlen=(DWORD)ln;*manifestOff=off;return TRUE;
}
static BOOL safe_rel_path(const char*p){SIZE_T i=0;if(!p||!*p||p[0]=='/'||p[0]=='\\')return FALSE;while(p[i]){if(p[i]==':'||p[i]=='\r'||p[i]=='\n'||p[i]=='\t')return FALSE;if(p[i]=='.'&&p[i+1]=='.'&&(p[i+2]=='/'||p[i+2]=='\\'||p[i+2]==0))return FALSE;i++;}return TRUE;}
static BOOL make_local_path(const WCHAR*root,const char*rel,WCHAR*out){char r8[2048],full8[4096];if(!safe_rel_path(rel))return FALSE;wide_to_utf8(root,r8,sizeof(r8));full8[0]=0;acat(full8,r8,sizeof(full8));acat(full8,"/",sizeof(full8));acat(full8,rel,sizeof(full8));utf8_to_wide(full8,out,MAX_PATH_W);return out[0]!=0;}
static BOOL payload_process(BOOL apply,DWORD*changed){
    BYTE*image=NULL,*manifest=NULL;DWORD len=0,mlen=0,pos=0;ULONGLONG moff=0;WCHAR root[MAX_PATH_W],bep[MAX_PATH_W];BOOL haveBep,ok=TRUE;*changed=0;
    if(!read_self_image(&image,&len))return FALSE;if(!payload_manifest(image,len,&manifest,&mlen,&moff)){pHeapFree(pGetProcessHeap(),0,image);return FALSE;}
    get_game_root(root,MAX_PATH_W);wcopy(bep,root,MAX_PATH_W);wcat(bep,(const WCHAR[]){'\\',0},MAX_PATH_W);wcat(bep,W_BEPINEX_DLL,MAX_PATH_W);haveBep=file_exists(bep);
    while(pos<mlen){DWORD st=pos,ln;char line[4096],*fields[5],*q;int fi=0;ULONGLONG off,sz;WCHAR local[MAX_PATH_W];
        while(pos<mlen&&manifest[pos]!='\n')pos++;ln=pos-st;if(pos<mlen)pos++;if(!ln)continue;if(ln>=sizeof(line)){ok=FALSE;break;}mcpy(line,manifest+st,ln);line[ln]=0;
        q=line;fields[fi++]=q;while(*q&&fi<5){if(*q=='\t'){*q=0;fields[fi++]=q+1;}q++;}if(fi!=5){ok=FALSE;break;}
        if(fields[0][0]=='M'&&haveBep)continue;if(fields[0][0]!='M'&&fields[0][0]!='A'){ok=FALSE;break;}
        off=parse_u64_dec(fields[2]);sz=parse_u64_dec(fields[3]);if(off+sz>moff||sz>0xFFFFFFFFULL){ok=FALSE;break;}if(!make_local_path(root,fields[4],local)){ok=FALSE;break;}
        if(needs_file(local,fields[1])){(*changed)++;if(apply){BYTE hh[32],expected[32];if(!hex_to_bytes(fields[1],expected,32)||!sha256(image+(DWORD)off,(DWORD)sz,hh)){ok=FALSE;break;}DWORD z;for(z=0;z<32;z++)if(hh[z]!=expected[z]){ok=FALSE;break;}if(!ok||!write_atomic(local,image+(DWORD)off,(DWORD)sz)){ok=FALSE;break;}}}
    }
    pHeapFree(pGetProcessHeap(),0,image);return ok;
}
static BOOL payload_needs_update(BOOL*needs){DWORD changed=0;BOOL ok=payload_process(FALSE,&changed);*needs=(ok&&changed>0);return ok;}
static BOOL payload_apply(void){DWORD changed=0;return payload_process(TRUE,&changed);}

static BOOL save_pending_launch(void){WCHAR cwd[MAX_PATH_W];LPWSTR cmd=pGetCommandLineW?pGetCommandLineW():NULL;if(!cmd)return FALSE;pGetCurrentDirectoryW(MAX_PATH_W,cwd);return set_reg_string((const WCHAR[]){'P','e','n','d','i','n','g','C','o','m','m','a','n','d',0},cmd)&&set_reg_string((const WCHAR[]){'P','e','n','d','i','n','g','C','w','d',0},cwd);}
static BOOL launch_updater(void){WCHAR self[MAX_PATH_W],sys[MAX_PATH_W],cmd[MAX_PATH_W*2];pGetModuleFileNameW(g_self,self,MAX_PATH_W);pGetSystemDirectoryW(sys,MAX_PATH_W);wcat(sys,(const WCHAR[]){'\\','r','u','n','d','l','l','3','2','.','e','x','e',0},MAX_PATH_W);cmd[0]=0;wcat(cmd,(const WCHAR[]){'"',0},MAX_PATH_W*2);wcat(cmd,sys,MAX_PATH_W*2);wcat(cmd,(const WCHAR[]){'"',' ','"',0},MAX_PATH_W*2);wcat(cmd,self,MAX_PATH_W*2);wcat(cmd,(const WCHAR[]){'"',',','A','u','t','o','M','o','d','S','y','n','c','R','u','n',0},MAX_PATH_W*2);STARTUPINFOW si;PROCESS_INFORMATION pi;mset(&si,0,sizeof(si));mset(&pi,0,sizeof(pi));si.cb=sizeof(si);BOOL ok=pCreateProcessW(NULL,cmd,NULL,NULL,FALSE,CREATE_UNICODE_ENVIRONMENT,NULL,NULL,&si,&pi);if(ok){pCloseHandle(pi.hThread);pCloseHandle(pi.hProcess);}return ok;}
static DWORD WINAPI kickoff_thread(LPVOID x){(void)x;pSleep(25);init_aux();BOOL needs=FALSE;if(payload_needs_update(&needs)&&needs){if(save_pending_launch()&&launch_updater())pExitProcess(0);}return 0;}
static BOOL is_valheim_process(void){WCHAR p[MAX_PATH_W];pGetModuleFileNameW(NULL,p,MAX_PATH_W);SIZE_T n=wslen(p),i=n;while(i&&p[i-1]!='\\'&&p[i-1]!='/')i--;return wicmp(p+i,W_VALHEIM)==0;}
static void relaunch_game(void){WCHAR cmd[MAX_PATH_W*4],cwd[MAX_PATH_W];if(!get_reg_string((const WCHAR[]){'P','e','n','d','i','n','g','C','o','m','m','a','n','d',0},cmd,MAX_PATH_W*4))return;if(!get_reg_string((const WCHAR[]){'P','e','n','d','i','n','g','C','w','d',0},cwd,MAX_PATH_W))cwd[0]=0;pSetEnvironmentVariableW(W_READY,(const WCHAR[]){'1',0});STARTUPINFOW si;PROCESS_INFORMATION pi;mset(&si,0,sizeof(si));mset(&pi,0,sizeof(pi));si.cb=sizeof(si);if(pCreateProcessW(NULL,cmd,NULL,NULL,FALSE,CREATE_UNICODE_ENVIRONMENT,NULL,cwd[0]?cwd:NULL,&si,&pi)){pCloseHandle(pi.hThread);pCloseHandle(pi.hProcess);}}

__declspec(dllexport) void CALLBACK AutoModSyncRun(HWND hwnd,HINSTANCE hinst,LPSTR cmd,int show){
    (void)hwnd;(void)hinst;(void)cmd;(void)show;
    if(!pLoadLibraryW) init_kernel();
    init_aux();
    if(pSleep) pSleep(750);
    if(payload_apply()) relaunch_game();
    else if(pMessageBoxW) pMessageBoxW(NULL,(const WCHAR[]){'A','u','t','o','M','o','d','S','y','n','c',' ','b','o','o','t','s','t','r','a','p',' ','i','n','s','t','a','l','l',' ','f','a','i','l','e','d','.',0},(const WCHAR[]){'V','a','l','h','e','i','m',' ','A','u','t','o','M','o','d','S','y','n','c',0},MB_OK|MB_ICONERROR);
}


// Proxy exports
__declspec(dllexport) BOOL WINAPI GetFileVersionInfoA(LPCSTR a,DWORD b,DWORD c,LPVOID d){ensure_real_version();return vGetFileVersionInfoA?vGetFileVersionInfoA(a,b,c,d):FALSE;}
__declspec(dllexport) BOOL WINAPI GetFileVersionInfoW(LPCWSTR a,DWORD b,DWORD c,LPVOID d){ensure_real_version();return vGetFileVersionInfoW?vGetFileVersionInfoW(a,b,c,d):FALSE;}
__declspec(dllexport) BOOL WINAPI GetFileVersionInfoExA(DWORD a,LPCSTR b,DWORD c,DWORD d,LPVOID e){ensure_real_version();return vGetFileVersionInfoExA?vGetFileVersionInfoExA(a,b,c,d,e):FALSE;}
__declspec(dllexport) BOOL WINAPI GetFileVersionInfoExW(DWORD a,LPCWSTR b,DWORD c,DWORD d,LPVOID e){ensure_real_version();return vGetFileVersionInfoExW?vGetFileVersionInfoExW(a,b,c,d,e):FALSE;}
__declspec(dllexport) DWORD WINAPI GetFileVersionInfoSizeA(LPCSTR a,LPDWORD b){ensure_real_version();return vGetFileVersionInfoSizeA?vGetFileVersionInfoSizeA(a,b):0;}
__declspec(dllexport) DWORD WINAPI GetFileVersionInfoSizeW(LPCWSTR a,LPDWORD b){ensure_real_version();return vGetFileVersionInfoSizeW?vGetFileVersionInfoSizeW(a,b):0;}
__declspec(dllexport) DWORD WINAPI GetFileVersionInfoSizeExA(DWORD a,LPCSTR b,LPDWORD c){ensure_real_version();return vGetFileVersionInfoSizeExA?vGetFileVersionInfoSizeExA(a,b,c):0;}
__declspec(dllexport) DWORD WINAPI GetFileVersionInfoSizeExW(DWORD a,LPCWSTR b,LPDWORD c){ensure_real_version();return vGetFileVersionInfoSizeExW?vGetFileVersionInfoSizeExW(a,b,c):0;}
__declspec(dllexport) BOOL WINAPI GetFileVersionInfoByHandle(DWORD a,HANDLE b,LPVOID*c,LPDWORD d){ensure_real_version();return vGetFileVersionInfoByHandle?vGetFileVersionInfoByHandle(a,b,c,d):FALSE;}
__declspec(dllexport) DWORD WINAPI VerFindFileA(DWORD a,LPCSTR b,LPCSTR c,LPCSTR d,LPSTR e,PUINT f,LPSTR g,PUINT h){ensure_real_version();return vVerFindFileA?vVerFindFileA(a,b,c,d,e,f,g,h):0;}
__declspec(dllexport) DWORD WINAPI VerFindFileW(DWORD a,LPCWSTR b,LPCWSTR c,LPCWSTR d,LPWSTR e,PUINT f,LPWSTR g,PUINT h){ensure_real_version();return vVerFindFileW?vVerFindFileW(a,b,c,d,e,f,g,h):0;}
__declspec(dllexport) DWORD WINAPI VerInstallFileA(DWORD a,LPCSTR b,LPCSTR c,LPCSTR d,LPCSTR e,LPCSTR f,LPSTR g,PUINT h){ensure_real_version();return vVerInstallFileA?vVerInstallFileA(a,b,c,d,e,f,g,h):0;}
__declspec(dllexport) DWORD WINAPI VerInstallFileW(DWORD a,LPCWSTR b,LPCWSTR c,LPCWSTR d,LPCWSTR e,LPCWSTR f,LPWSTR g,PUINT h){ensure_real_version();return vVerInstallFileW?vVerInstallFileW(a,b,c,d,e,f,g,h):0;}
__declspec(dllexport) DWORD WINAPI VerLanguageNameA(DWORD a,LPSTR b,DWORD c){ensure_real_version();return vVerLanguageNameA?vVerLanguageNameA(a,b,c):0;}
__declspec(dllexport) DWORD WINAPI VerLanguageNameW(DWORD a,LPWSTR b,DWORD c){ensure_real_version();return vVerLanguageNameW?vVerLanguageNameW(a,b,c):0;}
__declspec(dllexport) BOOL WINAPI VerQueryValueA(LPCVOID a,LPCSTR b,LPVOID*c,PUINT d){ensure_real_version();return vVerQueryValueA?vVerQueryValueA(a,b,c,d):FALSE;}
__declspec(dllexport) BOOL WINAPI VerQueryValueW(LPCVOID a,LPCWSTR b,LPVOID*c,PUINT d){ensure_real_version();return vVerQueryValueW?vVerQueryValueW(a,b,c,d):FALSE;}

BOOL WINAPI DllMain(HINSTANCE h,DWORD reason,LPVOID reserved){(void)reserved;if(reason==DLL_PROCESS_ATTACH){g_self=h;init_kernel();if(pDisableThreadLibraryCalls)pDisableThreadLibraryCalls(h);if(!pLoadLibraryW||!pGetModuleFileNameW||!pGetEnvironmentVariableW||!pCreateThread)return TRUE;if(is_valheim_process()){WCHAR r[8];if(!pGetEnvironmentVariableW(W_READY,r,8)){HANDLE t=pCreateThread(NULL,0,kickoff_thread,NULL,0,NULL);if(t&&pCloseHandle)pCloseHandle(t);}}}return TRUE;}

// satisfy possible compiler stack probe symbol if emitted; functions avoid large frames.
void __chkstk(void){}
