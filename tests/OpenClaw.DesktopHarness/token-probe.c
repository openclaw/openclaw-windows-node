#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <sddl.h>
#include <stdio.h>
#include <stdlib.h>
#include <wchar.h>

#if defined(OPENCLAW_FAIL_ATTACH_DLL)
__declspec(dllexport) void require_failure_fixture(void) {}
BOOL WINAPI DllMain(HINSTANCE module, DWORD reason, LPVOID reserved)
{
    (void)module;
    (void)reserved;
    return reason != DLL_PROCESS_ATTACH;
}
#elif defined(OPENCLAW_FAIL_ATTACH_EXE)
__declspec(dllimport) void require_failure_fixture(void);
int main(void)
{
    require_failure_fixture();
    return 0;
}
#else

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
        if (kind == TokenOwner) sid = ((TOKEN_OWNER *)buffer)->Owner;
        if (kind == TokenIntegrityLevel) sid = ((TOKEN_MANDATORY_LABEL *)buffer)->Label.Sid;
        if (kind == TokenAppContainerSid) sid = ((TOKEN_APPCONTAINER_INFORMATION *)buffer)->TokenAppContainer;
        printf(",\"%s\":", name);
        sid_value(sid);
    } else {
        printf(",\"%sError\":%lu", name, GetLastError());
    }
    free(buffer);
}

static void token_groups(HANDLE token, TOKEN_INFORMATION_CLASS kind, const char *name)
{
    DWORD size = 0;
    SetLastError(0);
    GetTokenInformation(token, kind, NULL, 0, &size);
    if (!size) {
        printf(",\"%sError\":%lu", name, GetLastError());
        return;
    }
    TOKEN_GROUPS *groups = (TOKEN_GROUPS *)malloc(size);
    if (!groups) ExitProcess(ERROR_NOT_ENOUGH_MEMORY);
    if (GetTokenInformation(token, kind, groups, size, &size)) {
        printf(",\"%s\":[", name);
        for (DWORD i = 0; i < groups->GroupCount; i++) {
            if (i) putchar(',');
            printf("{\"sid\":");
            sid_value(groups->Groups[i].Sid);
            printf(",\"attributes\":%lu,\"denyOnly\":%s}", groups->Groups[i].Attributes,
                (groups->Groups[i].Attributes & SE_GROUP_USE_FOR_DENY_ONLY) ? "true" : "false");
        }
        putchar(']');
    } else printf(",\"%sError\":%lu", name, GetLastError());
    free(groups);
}

static void token_default_dacl(HANDLE token)
{
    DWORD size = 0;
    GetTokenInformation(token, TokenDefaultDacl, NULL, 0, &size);
    if (!size) {
        printf(",\"defaultDaclError\":%lu", GetLastError());
        return;
    }
    TOKEN_DEFAULT_DACL *value = (TOKEN_DEFAULT_DACL *)malloc(size);
    if (!value) ExitProcess(ERROR_NOT_ENOUGH_MEMORY);
    if (GetTokenInformation(token, TokenDefaultDacl, value, size, &size)) {
        SECURITY_DESCRIPTOR descriptor;
        LPWSTR sddl = NULL;
        InitializeSecurityDescriptor(&descriptor, SECURITY_DESCRIPTOR_REVISION);
        SetSecurityDescriptorDacl(&descriptor, TRUE, value->DefaultDacl, FALSE);
        if (ConvertSecurityDescriptorToStringSecurityDescriptorW(&descriptor, 1, DACL_SECURITY_INFORMATION, &sddl, NULL)) {
            printf(",\"defaultDaclSddl\":");
            json_wide(sddl);
            LocalFree(sddl);
        } else printf(",\"defaultDaclConversionError\":%lu", GetLastError());
    } else printf(",\"defaultDaclError\":%lu", GetLastError());
    free(value);
}

static BOOL parse_descriptors(int argc, wchar_t **argv,
    const wchar_t **current, const wchar_t **reference, const wchar_t **station)
{
    *current = *reference = *station = NULL;
    if (argc != 5 && argc != 7) return FALSE;
    for (int i = 1; i < argc; i += 2) {
        const wchar_t **target = NULL;
        if (!wcscmp(argv[i], L"--current-sddl")) target = current;
        else if (!wcscmp(argv[i], L"--default-sddl")) target = reference;
        else if (!wcscmp(argv[i], L"--station-sddl")) target = station;
        if (!target || *target) return FALSE;
        *target = argv[i + 1];
    }
    return *current && *reference;
}

static int test_descriptor_arguments(void)
{
    const wchar_t *current, *reference, *station;
    wchar_t *valid[] = {L"probe", L"--station-sddl", L"station",
        L"--default-sddl", L"reference", L"--current-sddl", L"current"};
    if (!parse_descriptors(7, valid, &current, &reference, &station) ||
        wcscmp(current, L"current") || wcscmp(reference, L"reference") || wcscmp(station, L"station")) return 1;
    wchar_t *legacy[] = {L"probe", L"--current-sddl", L"current", L"--default-sddl", L"reference"};
    if (!parse_descriptors(5, legacy, &current, &reference, &station) || station) return 2;
    wchar_t *duplicate[] = {L"probe", L"--current-sddl", L"a", L"--current-sddl", L"b"};
    if (parse_descriptors(5, duplicate, &current, &reference, &station)) return 3;
    wchar_t *unknown[] = {L"probe", L"--other", L"a", L"--default-sddl", L"b"};
    if (parse_descriptors(5, unknown, &current, &reference, &station)) return 4;
    if (parse_descriptors(4, legacy, &current, &reference, &station)) return 5;
    printf("PASS: 5 native-free descriptor argument regressions.\n");
    return 0;
}

static void object_access(HANDLE token, const wchar_t *sddl, const char *name, BOOL station)
{
    PSECURITY_DESCRIPTOR descriptor = NULL;
    HANDLE impersonation = NULL;
    if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, &descriptor, NULL)) {
        printf(",\"%sDescriptorError\":%lu", name, GetLastError());
        return;
    }
    if (!DuplicateToken(token, SecurityImpersonation, &impersonation)) {
        printf(",\"%sTokenError\":%lu", name, GetLastError());
        LocalFree(descriptor);
        return;
    }
    GENERIC_MAPPING mapping = {
        READ_CONTROL | DESKTOP_READOBJECTS | DESKTOP_ENUMERATE,
        READ_CONTROL | DESKTOP_WRITEOBJECTS | DESKTOP_CREATEWINDOW | DESKTOP_CREATEMENU |
            DESKTOP_HOOKCONTROL | DESKTOP_JOURNALRECORD | DESKTOP_JOURNALPLAYBACK,
        READ_CONTROL | DESKTOP_SWITCHDESKTOP,
        STANDARD_RIGHTS_REQUIRED | DESKTOP_READOBJECTS | DESKTOP_CREATEWINDOW | DESKTOP_CREATEMENU |
            DESKTOP_HOOKCONTROL | DESKTOP_JOURNALRECORD | DESKTOP_JOURNALPLAYBACK |
            DESKTOP_ENUMERATE | DESKTOP_WRITEOBJECTS | DESKTOP_SWITCHDESKTOP
    };
    const DWORD desktopRights[] = {DESKTOP_READOBJECTS, DESKTOP_WRITEOBJECTS, DESKTOP_CREATEWINDOW,
        DESKTOP_CREATEMENU, DESKTOP_HOOKCONTROL, DESKTOP_JOURNALRECORD, DESKTOP_JOURNALPLAYBACK,
        DESKTOP_ENUMERATE, DESKTOP_SWITCHDESKTOP, READ_CONTROL};
    const char *desktopLabels[] = {"readObjects", "writeObjects", "createWindow", "createMenu",
        "hookControl", "journalRecord", "journalPlayback", "enumerate", "switchDesktop", "readControl"};
    const DWORD stationRights[] = {WINSTA_ENUMDESKTOPS, WINSTA_READATTRIBUTES, WINSTA_ACCESSCLIPBOARD,
        WINSTA_CREATEDESKTOP, WINSTA_WRITEATTRIBUTES, WINSTA_ACCESSGLOBALATOMS, WINSTA_EXITWINDOWS,
        WINSTA_ENUMERATE, WINSTA_READSCREEN, READ_CONTROL};
    const char *stationLabels[] = {"enumDesktops", "readAttributes", "accessClipboard", "createDesktop",
        "writeAttributes", "accessGlobalAtoms", "exitWindows", "enumerate", "readScreen", "readControl"};
    if (station) {
        mapping.GenericRead = READ_CONTROL | WINSTA_ENUMDESKTOPS | WINSTA_ENUMERATE |
            WINSTA_READATTRIBUTES | WINSTA_READSCREEN;
        mapping.GenericWrite = READ_CONTROL | WINSTA_ACCESSCLIPBOARD | WINSTA_CREATEDESKTOP | WINSTA_WRITEATTRIBUTES;
        mapping.GenericExecute = READ_CONTROL | WINSTA_ACCESSGLOBALATOMS | WINSTA_EXITWINDOWS;
        mapping.GenericAll = WINSTA_ALL_ACCESS;
    }
    const DWORD *rights = station ? stationRights : desktopRights;
    const char **labels = station ? stationLabels : desktopLabels;
    printf(",\"%s\":{", name);
    printf("\"suppliedDescriptorSddl\":");
    json_wide(sddl);
    printf(",\"binding\":\"caller-supplied descriptor, not independently proven SDK-assigned object\"");
    for (int index = 0; index < 10; index++) {
        BYTE privilegeBuffer[1024];
        DWORD size = sizeof(privilegeBuffer), granted = 0;
        BOOL access = FALSE;
        putchar(',');
        SetLastError(0);
        BOOL checked = AccessCheck(descriptor, impersonation, rights[index], &mapping,
            (PPRIVILEGE_SET)privilegeBuffer, &size, &granted, &access);
        DWORD error = checked ? 0 : GetLastError();
        if (checked) {
            printf("\"%s\":%s", labels[index], access ? "true" : "false");
        } else {
            printf("\"%sError\":%lu", labels[index], error);
        }
        printf(",\"%sEvidence\":{\"desiredMask\":%lu,\"apiSuccess\":%s,\"nativeError\":%lu,"
            "\"grantedMask\":%lu,\"accessStatus\":%s}", labels[index], rights[index],
            checked ? "true" : "false", error, granted, access ? "true" : "false");
    }
    putchar('}');
    CloseHandle(impersonation);
    LocalFree(descriptor);
}

static void assigned_object(HMODULE user32, HANDLE object, const char *name)
{
    typedef BOOL (WINAPI *GET_INFO)(HANDLE, int, PVOID, DWORD, LPDWORD);
    typedef BOOL (WINAPI *GET_SECURITY)(HANDLE, PSECURITY_INFORMATION, PSECURITY_DESCRIPTOR, DWORD, LPDWORD);
    GET_INFO getInfo = (GET_INFO)GetProcAddress(user32, "GetUserObjectInformationW");
    GET_SECURITY getSecurity = (GET_SECURITY)GetProcAddress(user32, "GetUserObjectSecurity");
    printf(",\"%s\":{", name);
    if (!object || !getInfo || !getSecurity) {
        printf("\"available\":false}");
        return;
    }
    wchar_t objectName[512] = {0};
    DWORD needed = 0;
    SetLastError(0);
    if (getInfo(object, UOI_NAME, objectName, sizeof(objectName), &needed)) {
        printf("\"name\":");
        json_wide(objectName);
    } else printf("\"nameError\":%lu", GetLastError());
    SECURITY_INFORMATION information = OWNER_SECURITY_INFORMATION | GROUP_SECURITY_INFORMATION |
        DACL_SECURITY_INFORMATION | LABEL_SECURITY_INFORMATION;
    SetLastError(0);
    getSecurity(object, &information, NULL, 0, &needed);
    DWORD sizeError = GetLastError();
    printf(",\"securitySizeQueryError\":%lu", sizeError);
    if (needed) {
        PSECURITY_DESCRIPTOR descriptor = malloc(needed);
        if (!descriptor) ExitProcess(ERROR_NOT_ENOUGH_MEMORY);
        SetLastError(0);
        if (getSecurity(object, &information, descriptor, needed, &needed)) {
            LPWSTR sddl = NULL;
            if (ConvertSecurityDescriptorToStringSecurityDescriptorW(descriptor, 1, information, &sddl, NULL)) {
                printf(",\"sddl\":");
                json_wide(sddl);
                LocalFree(sddl);
            } else printf(",\"conversionError\":%lu", GetLastError());
        } else printf(",\"securityError\":%lu", GetLastError());
        free(descriptor);
    }
    putchar('}');
}

static int ownership_wait(wchar_t **argv)
{
    HANDLE ready = OpenEventW(EVENT_MODIFY_STATE, FALSE, argv[2]);
    if (!ready) return (int)GetLastError();
    HANDLE release = OpenEventW(SYNCHRONIZE, FALSE, argv[3]);
    if (!release) {
        DWORD error = GetLastError();
        CloseHandle(ready);
        return (int)error;
    }
    FILE *receipt = NULL;
    if (_wfreopen_s(&receipt, argv[4], L"w", stdout)) {
        CloseHandle(ready);
        CloseHandle(release);
        return ERROR_WRITE_FAULT;
    }
    FILETIME created, exited, kernel, user;
    if (!GetProcessTimes(GetCurrentProcess(), &created, &exited, &kernel, &user))
        return (int)GetLastError();
    wchar_t executable[32768];
    if (!GetModuleFileNameW(NULL, executable, 32768)) return (int)GetLastError();
    printf("{\"processId\":%lu,\"processStartFileTime\":%llu,\"errorMode\":%lu,\"executable\":",
        GetCurrentProcessId(),
        ((unsigned long long)created.dwHighDateTime << 32) | created.dwLowDateTime,
        GetErrorMode());
    json_wide(executable);
    printf("}\n");
    fclose(receipt);
    if (!SetEvent(ready)) return (int)GetLastError();
    DWORD wait = WaitForSingleObject(release, INFINITE);
    CloseHandle(ready);
    CloseHandle(release);
    return wait == WAIT_OBJECT_0 ? 0 : (int)GetLastError();
}

static int ownership_tree(wchar_t **argv)
{
    HANDLE childReady = OpenEventW(SYNCHRONIZE, FALSE, argv[3]);
    if (!childReady) return (int)GetLastError();
    wchar_t executable[32768], command[32768];
    if (!GetModuleFileNameW(NULL, executable, 32768)) return (int)GetLastError();
    if (swprintf_s(command, 32768, L"\"%ls\" ownership-wait \"%ls\" \"%ls\" \"%ls\"",
        executable, argv[3], argv[4], argv[6]) < 0) return ERROR_INVALID_PARAMETER;
    STARTUPINFOW inherited = {0}, startup = {0};
    PROCESS_INFORMATION child = {0};
    GetStartupInfoW(&inherited);
    startup.cb = sizeof(startup);
    startup.lpDesktop = inherited.lpDesktop;
    if (!CreateProcessW(executable, command, NULL, NULL, FALSE, CREATE_NO_WINDOW,
        NULL, NULL, &startup, &child)) {
        DWORD error = GetLastError();
        CloseHandle(childReady);
        return (int)error;
    }
    DWORD ready = WaitForSingleObject(childReady, INFINITE);
    wchar_t *rootArguments[] = {argv[0], L"ownership-wait", argv[2], argv[4], argv[5]};
    int result = ready == WAIT_OBJECT_0 ? ownership_wait(rootArguments) : (int)GetLastError();
    if (result) TerminateProcess(child.hProcess, (UINT)result);
    WaitForSingleObject(child.hProcess, INFINITE);
    CloseHandle(child.hThread);
    CloseHandle(child.hProcess);
    CloseHandle(childReady);
    return result;
}

int wmain(int argc, wchar_t **argv)
{
    if (argc == 2 && !wcscmp(argv[1], L"--test-descriptor-arguments"))
        return test_descriptor_arguments();
    if (argc == 7 && wcscmp(argv[1], L"ownership-tree") == 0)
        return ownership_tree(argv);
    if (argc == 5 && wcscmp(argv[1], L"ownership-wait") == 0)
        return ownership_wait(argv);
    if (argc == 3 && wcscmp(argv[1], L"--error-mode") == 0) {
        FILE *receipt = NULL;
        if (_wfopen_s(&receipt, argv[2], L"w")) return ERROR_WRITE_FAULT;
        fprintf(receipt, "{\"errorMode\":%lu}\n", GetErrorMode());
        fclose(receipt);
        return 0;
    }
    STARTUPINFOW startup = {0};
    DWORD session = 0;
    PROCESS_MITIGATION_SYSTEM_CALL_DISABLE_POLICY policy = {0};
    HANDLE token = NULL;
    GetStartupInfoW(&startup);
    ProcessIdToSessionId(GetCurrentProcessId(), &session);
    printf("{\"phase\":\"before-user32\",\"pid\":%lu,\"sessionId\":%lu,\"startupDesktop\":",
        GetCurrentProcessId(), session);
    json_wide(startup.lpDesktop);
    wchar_t cwd[32768];
    DWORD cwdLength = GetCurrentDirectoryW(32768, cwd);
    if (cwdLength && cwdLength < 32768) {
        printf(",\"cwd\":");
        json_wide(cwd);
    } else printf(",\"cwdError\":%lu", cwdLength ? ERROR_INSUFFICIENT_BUFFER : GetLastError());
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
        token_sid(token, TokenOwner, "ownerSid");
        token_dword(token, TokenMandatoryPolicy, "mandatoryPolicy");
        token_groups(token, TokenGroups, "groups");
        token_groups(token, TokenRestrictedSids, "restrictedSids");
        token_groups(token, TokenCapabilities, "capabilities");
        token_default_dacl(token);
        const wchar_t *current, *reference, *station;
        if (parse_descriptors(argc, argv, &current, &reference, &station)) {
            object_access(token, current, "currentDesktopDaclAccess", FALSE);
            object_access(token, reference, "defaultDesktopDaclAccess", FALSE);
            if (station) object_access(token, station, "callerStationDaclAccess", TRUE);
        } else if (argc > 1 && wcscmp(argv[1], L"--token-only")) {
            printf(",\"descriptorArgumentsValid\":false");
        }
        CloseHandle(token);
    } else {
        printf(",\"tokenError\":%lu", GetLastError());
    }
    printf("}\n");
    fflush(stdout);
    if (argc == 2 && !wcscmp(argv[1], L"--token-only")) return 0;

    SetLastError(0);
    HMODULE user32 = LoadLibraryExW(L"user32.dll", NULL, LOAD_LIBRARY_SEARCH_SYSTEM32);
    DWORD loadError = user32 ? 0 : GetLastError();
    printf("{\"phase\":\"after-user32\",\"loaded\":%s,\"loadError\":%lu",
        user32 ? "true" : "false", loadError);
    if (user32) {
        typedef HDESK (WINAPI *GET_THREAD_DESKTOP)(DWORD);
        typedef HWINSTA (WINAPI *GET_STATION)(void);
        typedef BOOL (WINAPI *GET_OBJECT_INFO)(HANDLE, int, PVOID, DWORD, LPDWORD);
        GET_THREAD_DESKTOP getDesktop = (GET_THREAD_DESKTOP)GetProcAddress(user32, "GetThreadDesktop");
        GET_OBJECT_INFO getInfo = (GET_OBJECT_INFO)GetProcAddress(user32, "GetUserObjectInformationW");
        GET_STATION getStation = (GET_STATION)GetProcAddress(user32, "GetProcessWindowStation");
        if (getDesktop) assigned_object(user32, getDesktop(GetCurrentThreadId()), "assignedDesktop");
        if (getStation) assigned_object(user32, getStation(), "assignedStation");
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
    } else {
        printf(",\"assignedObjectIdentity\":\"unknown: USER32 initialization failed; caller descriptors are not assigned-object proof\"");
    }
    printf("}\n");
    fflush(stdout);
    return loadError ? (int)loadError : 0;
}
#endif
