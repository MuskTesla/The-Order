// SPDX-License-Identifier: AGPL-3.0-or-later
// A GUI entry for the manifest-checked release launcher.
// CreateProcess receives a program and argv, never a shell command expression.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shellapi.h>
#include <string>
#include <vector>

static std::wstring Quote(const std::wstring& value) {
    std::wstring result = L"\"";
    size_t slashes = 0;
    for (wchar_t ch : value) {
        if (ch == L'\\') { ++slashes; continue; }
        result.append(slashes * (ch == L'\"' ? 2 : 1), L'\\');
        slashes = 0;
        if (ch == L'\"') result += L'\\';
        result += ch;
    }
    result.append(slashes * 2, L'\\');
    return result + L'\"';
}

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int) {
    bool validate = false;
    bool testRequested = false;
    std::wstring testRoot;
    int argc = 0;
    wchar_t** argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    if (!argv) return 1;
    bool valid = true;
    for (int i = 1; i < argc; ++i) {
        if (std::wstring(argv[i]) == L"--validate-only" && !validate) validate = true;
        else if (std::wstring(argv[i]) == L"--test-profile" && !testRequested && i + 1 < argc && argv[i + 1][0] != L'\0') {
            testRequested = true;
            testRoot = argv[++i];
        }
        else { valid = false; break; }
    }
    LocalFree(argv);
    if (!valid) return 2;

    std::vector<wchar_t> buffer(32768);
    DWORD length = GetModuleFileNameW(nullptr, buffer.data(), static_cast<DWORD>(buffer.size()));
    if (length == 0 || length >= buffer.size()) return 1;
    std::wstring directory(buffer.data(), length);
    directory.erase(directory.find_last_of(L"\\/"));
    length = GetSystemDirectoryW(buffer.data(), static_cast<UINT>(buffer.size()));
    if (length == 0 || length >= buffer.size()) return 1;
    std::wstring powershell(buffer.data(), length);
    powershell += L"\\WindowsPowerShell\\v1.0\\powershell.exe";
    std::wstring command = Quote(powershell) + L" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File " + Quote(directory + L"\\launch.ps1");
    if (!testRoot.empty()) command += L" -Test -DataDirectory " + Quote(testRoot);
    if (validate) command += L" -ValidateOnly";

    STARTUPINFOW startup{};
    startup.cb = sizeof(startup);
    startup.dwFlags = STARTF_USESHOWWINDOW;
    startup.wShowWindow = SW_HIDE;
    PROCESS_INFORMATION child{};
    DWORD exitCode = 1;
    if (CreateProcessW(powershell.c_str(), command.data(), nullptr, nullptr, FALSE,
            CREATE_NO_WINDOW, nullptr, directory.c_str(), &startup, &child)) {
        CloseHandle(child.hThread);
        WaitForSingleObject(child.hProcess, INFINITE);
        GetExitCodeProcess(child.hProcess, &exitCode);
        CloseHandle(child.hProcess);
    }
    if (exitCode != 0 && !validate)
        MessageBoxW(nullptr,
            L"The Order 未能启动。请正常退出托盘中的旧实例，或重新运行安装包修复程序文件。\n\n修复安装保留书架资料；卸载时可选择是否保留数据。",
            L"The Order", MB_OK | MB_ICONERROR);
    return static_cast<int>(exitCode);
}
