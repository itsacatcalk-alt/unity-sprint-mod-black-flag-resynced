// ModActions.dll - installer custom actions for the Unity Sprint mod (JARVIS, 5 Nov 2026; v1.0.4).
// Replaces v1.0.3's CustomActions.vbs one for one, without VBScript, WMI or WScript.Shell:
//   CheckGameDir  (wizard, immediate): the chosen folder must hold the game and the game must not be running.
//                 Sets GAMEDIR_OK ("1"/"0") and GAMEDIR_MSG.
//   ApplyMod / RemoveMod / RollbackMod (deferred): run SprintPatch.exe (install / uninstall / rollback),
//                 relay its "P|permille|eta|text" lines to the progress bar and status line, and show its
//                 "E|message" text if it fails. CustomActionData is "<path of SprintPatch.exe>|<game folder>".
// Built by build.ps1 with the Visual C++ compiler (static runtime, no extra DLLs needed).
#include <windows.h>
#include <msi.h>
#include <msiquery.h>
#include <tlhelp32.h>
#include <string>

static const wchar_t* FORGE = L"DataPC_boot_patch_01.forge";
static const wchar_t* GAMEEXE = L"ACBlackFlag.exe";

static std::wstring GetProp(MSIHANDLE h, const wchar_t* name)
{
    wchar_t empty[1] = L"";
    DWORD n = 0;
    if (MsiGetPropertyW(h, name, empty, &n) != ERROR_MORE_DATA) return L"";
    std::wstring s(n + 1, L'\0');
    n = (DWORD)s.size();
    if (MsiGetPropertyW(h, name, &s[0], &n) != ERROR_SUCCESS) return L"";
    s.resize(n);
    return s;
}

static bool FileExists(const std::wstring& path)
{
    DWORD a = GetFileAttributesW(path.c_str());
    return a != INVALID_FILE_ATTRIBUTES && !(a & FILE_ATTRIBUTE_DIRECTORY);
}

static bool GameRunning()
{
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snap == INVALID_HANDLE_VALUE) return false;
    PROCESSENTRY32W pe = { sizeof(pe) };
    bool found = false;
    for (BOOL more = Process32FirstW(snap, &pe); more && !found; more = Process32NextW(snap, &pe))
        if (_wcsicmp(pe.szExeFile, GAMEEXE) == 0) found = true;
    CloseHandle(snap);
    return found;
}

extern "C" __declspec(dllexport) UINT __stdcall CheckGameDir(MSIHANDLE h)
{
    std::wstring dir = GetProp(h, L"GAMEDIR");
    if (!dir.empty() && dir.back() != L'\\') dir += L'\\';
    MsiSetPropertyW(h, L"GAMEDIR_OK", L"0");
    if (!FileExists(dir + FORGE) || !FileExists(dir + GAMEEXE))
    {
        std::wstring msg = L"This folder does not contain Assassin's Creed Black Flag Resynced. Please choose the game's installation folder (the one with ";
        msg += GAMEEXE;
        msg += L" in it).";
        MsiSetPropertyW(h, L"GAMEDIR_MSG", msg.c_str());
        return ERROR_SUCCESS;
    }
    if (GameRunning())
        MsiSetPropertyW(h, L"GAMEDIR_MSG", L"The game is running. Please close it completely, then click Next again.");
    else
        MsiSetPropertyW(h, L"GAMEDIR_OK", L"1");
    return ERROR_SUCCESS;
}

static std::wstring FormatEta(long s)
{
    if (s < 5) return L"a few seconds";
    if (s < 90) return std::to_wstring(s) + L" seconds";
    return std::to_wstring((s + 30) / 60) + L" minutes";
}

static void Progress(MSIHANDLE h, int f1, int f2, int f3, int f4, int fields)
{
    PMSIHANDLE rec = MsiCreateRecord(fields);
    MsiRecordSetInteger(rec, 1, f1);
    MsiRecordSetInteger(rec, 2, f2);
    if (fields >= 3) MsiRecordSetInteger(rec, 3, f3);
    if (fields >= 4) MsiRecordSetInteger(rec, 4, f4);
    MsiProcessMessage(h, INSTALLMESSAGE_PROGRESS, rec);
}

static void Status(MSIHANDLE h, const std::wstring& text)
{
    PMSIHANDLE rec = MsiCreateRecord(1);
    MsiRecordSetStringW(rec, 1, text.c_str());
    MsiProcessMessage(h, INSTALLMESSAGE_ACTIONDATA, rec);
}

// Splits "a|b|c|d" into at most max parts (the last part keeps any further '|').
static int Split(const std::wstring& line, std::wstring* parts, int max)
{
    int n = 0; size_t start = 0;
    while (n < max - 1)
    {
        size_t bar = line.find(L'|', start);
        if (bar == std::wstring::npos) break;
        parts[n++] = line.substr(start, bar - start);
        start = bar + 1;
    }
    parts[n++] = line.substr(start);
    return n;
}

static UINT RunEngine(MSIHANDLE h, const wchar_t* mode, bool showErrors)
{
    std::wstring data = GetProp(h, L"CustomActionData");
    size_t bar = data.find(L'|');
    std::wstring exe = bar == std::wstring::npos ? data : data.substr(0, bar);
    std::wstring dir = bar == std::wstring::npos ? L"" : data.substr(bar + 1);
    while (!dir.empty() && dir.back() == L'\\') dir.pop_back();

    Progress(h, 0, 1000, 0, 0, 4);

    bool ok = false;
    std::wstring msg = L"The installer could not finish.";
    DWORD exitCode = 1;

    SECURITY_ATTRIBUTES sa = { sizeof(sa), nullptr, TRUE };
    HANDLE readEnd = nullptr, writeEnd = nullptr;
    if (CreatePipe(&readEnd, &writeEnd, &sa, 0))
    {
        SetHandleInformation(readEnd, HANDLE_FLAG_INHERIT, 0);
        STARTUPINFOW si = { sizeof(si) };
        si.dwFlags = STARTF_USESTDHANDLES;
        si.hStdOutput = writeEnd;
        si.hStdError = writeEnd;
        si.hStdInput = nullptr;
        PROCESS_INFORMATION pi = {};
        std::wstring cmd = L"\"" + exe + L"\" " + mode + L" \"" + dir + L"\"";
        if (CreateProcessW(exe.c_str(), &cmd[0], nullptr, nullptr, TRUE, CREATE_NO_WINDOW, nullptr, nullptr, &si, &pi))
        {
            CloseHandle(writeEnd); writeEnd = nullptr;
            std::string pending;
            char buf[4096];
            DWORD got = 0;
            int lastPm = 0;
            auto handleLine = [&](std::string raw)
            {
                if (!raw.empty() && raw.back() == '\r') raw.pop_back();
                int wn = MultiByteToWideChar(CP_OEMCP, 0, raw.c_str(), (int)raw.size(), nullptr, 0);
                std::wstring line(wn, L'\0');
                if (wn > 0) MultiByteToWideChar(CP_OEMCP, 0, raw.c_str(), (int)raw.size(), &line[0], wn);
                if (line.compare(0, 2, L"E|") == 0) { msg = line.substr(2); return; }
                std::wstring f[4];
                int n = Split(line, f, 4);
                if (n < 2) return;
                if (f[0] == L"P" && n == 4)
                {
                    int pm = _wtoi(f[1].c_str());
                    long eta = _wtol(f[2].c_str());
                    std::wstring text = f[3];
                    if (pm > lastPm) { Progress(h, 2, pm - lastPm, 0, 0, 2); lastPm = pm; }
                    if (eta >= 0) text += L" (about " + FormatEta(eta) + L" left)";
                    Status(h, text);
                }
                else if (f[0] == L"OK") ok = true;
            };
            while (ReadFile(readEnd, buf, sizeof(buf), &got, nullptr) && got > 0)
            {
                pending.append(buf, got);
                size_t nl;
                while ((nl = pending.find('\n')) != std::string::npos)
                {
                    handleLine(pending.substr(0, nl));
                    pending.erase(0, nl + 1);
                }
            }
            if (!pending.empty()) handleLine(pending);
            WaitForSingleObject(pi.hProcess, INFINITE);
            GetExitCodeProcess(pi.hProcess, &exitCode);
            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
        }
        if (writeEnd) CloseHandle(writeEnd);
        CloseHandle(readEnd);
    }

    if (ok && exitCode == 0) return ERROR_SUCCESS;
    if (showErrors)
    {
        PMSIHANDLE rec = MsiCreateRecord(0);
        MsiRecordSetStringW(rec, 0, msg.c_str());
        MsiProcessMessage(h, INSTALLMESSAGE(INSTALLMESSAGE_ERROR | MB_OK | MB_ICONERROR), rec);
    }
    return ERROR_INSTALL_FAILURE;
}

extern "C" __declspec(dllexport) UINT __stdcall ApplyMod(MSIHANDLE h) { return RunEngine(h, L"install", true); }
extern "C" __declspec(dllexport) UINT __stdcall RemoveMod(MSIHANDLE h) { return RunEngine(h, L"uninstall", true); }
extern "C" __declspec(dllexport) UINT __stdcall RollbackMod(MSIHANDLE h) { RunEngine(h, L"rollback", false); return ERROR_SUCCESS; }

BOOL APIENTRY DllMain(HMODULE, DWORD, LPVOID) { return TRUE; }
