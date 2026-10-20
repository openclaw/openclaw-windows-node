#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <sddl.h>
#include <stdio.h>
#include <stdlib.h>

static void json_wide(const wchar_t *value)
{
    putchar('"');
    if (value) {
        while (*value) {
            unsigned int ch = (unsigned short)*value++;
            if (ch == '"' || ch == '\\') printf("\\%c", ch);
            else if (ch < 32 || ch > 126) printf("\\u%04x", ch);
            else putchar((int)ch);
        }
    }
    putchar('"');
}

static void sid_value(PSID sid)
{
    LPWSTR text = NULL;
    if (sid && ConvertSidToStringSidW(sid, &text)) {
        json_wide(text);
        LocalFree(text);
    } else {
        printf("null");
    }
}

static void token_dword(HANDLE token, TOKEN_INFORMATION_CLASS kind, const char *name)
{
    DWORD value = 0, size = 0;
    if (GetTokenInformation(token, kind, &value, sizeof(value), &size))
        printf(",\"%s\":%lu", name, value);
    else
        printf(",\"%sError\":%lu", name, GetLastError());
}

static void token_sid(HANDLE token, TOKEN_INFORMATION_CLASS kind, const char *name)
{
    DWORD size = 0;
    GetTokenInformation(token, kind, NULL, 0, &size);
    if (!size) {
        printf(",\"%sError\":%lu", name, GetLastError());
        return;
    }
    void *buffer = malloc(size);
    if (!buffer) ExitProcess(ERROR_NOT_ENOUGH_MEMORY);
    if (GetTokenInformation(token, kind, buffer, size, &size)) {
        PSID sid = NULL;
        if (kind == TokenUser) sid = ((TOKEN_USER *)buffer)->User.Sid;
        if (kind == TokenIntegrityLevel) sid = ((TOKEN_MANDATORY_LABEL *)buffer)->Label.Sid;
        if (kind == TokenAppContainerSid) sid = ((TOKEN_APPCONTAINER_INFORMATION *)buffer)->TokenAppContainer;
        printf(",\"%s\":", name);
        sid_value(sid);
    } else {
        printf(",\"%sError\":%lu", name, GetLastError());
    }
    free(buffer);
}

static void desktop_access(HANDLE token, const char *sddl, const char *name)
{
    PSECURITY_DESCRIPTOR descriptor = NULL;
    HANDLE impersonation = NULL;
    if (!ConvertStringSecurityDescriptorToSecurityDescriptorA(sddl, 1, &descriptor, NULL)) {
        printf(",\"%sDescriptorError\":%lu", name, GetLastError());
        return;
    }
    if (!DuplicateToken(token, SecurityImpersonation, &impersonation)) {
        printf(",\"%sTokenError\":%lu", name, GetLastError());
        LocalFree(descriptor);
        return;
    }
    GENERIC_MAPPING mapping = {0x20041, 0x200be, 0x20100, 0xf01ff};
    const DWORD rights[] = {0x1, 0x80, 0x2};
    const char *labels[] = {"readObjects", "writeObjects", "createWindow"};
    printf(",\"%s\":{", name);
    for (int index = 0; index < 3; index++) {
        BYTE privilegeBuffer[1024];
        DWORD size = sizeof(privilegeBuffer), granted = 0;
        BOOL access = FALSE;
        if (index) putchar(',');
        if (AccessCheck(descriptor, impersonation, rights[index], &mapping,
            (PPRIVILEGE_SET)privilegeBuffer, &size, &granted, &access)) {
            printf("\"%s\":%s", labels[index], access ? "true" : "false");
        } else {
            printf("\"%sError\":%lu", labels[index], GetLastError());
        }
    }
    putchar('}');
    CloseHandle(impersonation);
    LocalFree(descriptor);
}

int main(int argc, char **argv)
{
    STARTUPINFOW startup = {0};
    DWORD session = 0;
    PROCESS_MITIGATION_SYSTEM_CALL_DISABLE_POLICY policy = {0};
    HANDLE token = NULL;
    GetStartupInfoW(&startup);
    ProcessIdToSessionId(GetCurrentProcessId(), &session);
    printf("{\"phase\":\"before-user32\",\"pid\":%lu,\"sessionId\":%lu,\"startupDesktop\":",
        GetCurrentProcessId(), session);
    json_wide(startup.lpDesktop);
    if (GetProcessMitigationPolicy(GetCurrentProcess(), ProcessSystemCallDisablePolicy, &policy, sizeof(policy)))
        printf(",\"win32kMitigationFlags\":%lu", policy.Flags);
    else
        printf(",\"win32kMitigationError\":%lu", GetLastError());
    if (OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY | TOKEN_DUPLICATE, &token)) {
        token_dword(token, TokenSessionId, "tokenSessionId");
        token_dword(token, TokenIsAppContainer, "isAppContainer");
        token_dword(token, TokenElevationType, "elevationType");
        token_sid(token, TokenUser, "userSid");
        token_sid(token, TokenIntegrityLevel, "integritySid");
        token_sid(token, TokenAppContainerSid, "appContainerSid");
        if (argc == 5) {
            desktop_access(token, argv[2], "currentDesktopDaclAccess");
            desktop_access(token, argv[4], "defaultDesktopDaclAccess");
        }
        CloseHandle(token);
    } else {
        printf(",\"tokenError\":%lu", GetLastError());
    }
    printf("}\n");
    fflush(stdout);

    SetLastError(0);
    HMODULE user32 = LoadLibraryExW(L"user32.dll", NULL, LOAD_LIBRARY_SEARCH_SYSTEM32);
    DWORD loadError = user32 ? 0 : GetLastError();
    printf("{\"phase\":\"after-user32\",\"loaded\":%s,\"loadError\":%lu",
        user32 ? "true" : "false", loadError);
    if (user32) {
        typedef HDESK (WINAPI *GET_THREAD_DESKTOP)(DWORD);
        typedef BOOL (WINAPI *GET_OBJECT_INFO)(HANDLE, int, PVOID, DWORD, LPDWORD);
        GET_THREAD_DESKTOP getDesktop = (GET_THREAD_DESKTOP)GetProcAddress(user32, "GetThreadDesktop");
        GET_OBJECT_INFO getInfo = (GET_OBJECT_INFO)GetProcAddress(user32, "GetUserObjectInformationW");
        if (getDesktop && getInfo) {
            wchar_t name[512] = {0};
            DWORD needed = 0;
            if (getInfo(getDesktop(GetCurrentThreadId()), 2, name, sizeof(name), &needed)) {
                printf(",\"desktop\":");
                json_wide(name);
            } else {
                printf(",\"desktopError\":%lu", GetLastError());
            }
        }
        FreeLibrary(user32);
    }
    printf("}\n");
    fflush(stdout);
    return loadError ? (int)loadError : 0;
}
