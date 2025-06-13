#define _CRT_SECURE_NO_WARNINGS
#include <Windows.h>
#include <objbase.h>
#include <shlobj.h>
#include <cstring>
#include <new>
#include <string>
#include "ThemeLib/public/themetool.h"
#include "LongbridgeThemeCore.h"  // Contains all required GUIDs and interface definitions

#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "oleaut32.lib")
#pragma comment(lib, "uxtheme.lib")
#pragma comment(lib, "version.lib")
#pragma comment(lib, "vcruntime.lib")
#pragma comment(lib, "ucrt.lib")

// Implement minimal CRT functions
extern "C" errno_t __cdecl strcpy_s(
    char* _Destination,
    size_t _SizeInBytes,
    const char* _Source
) {
    if (_SizeInBytes == 0 || _Destination == nullptr) return 22;
    if (_Source == nullptr) {
        *_Destination = '\0';
        return 22;
    }
    char* p = _Destination;
    size_t available = _SizeInBytes;
    while ((*p++ = *_Source++) != 0 && --available > 0) {}
    if (available == 0) {
        *_Destination = '\0';
        return 80;
    }
    return 0;
}

extern "C" errno_t __cdecl strcat_s(
    char* _Destination,
    size_t _SizeInBytes,
    const char* _Source
) {
    if (_Destination == nullptr || _SizeInBytes == 0) return 22;
    if (_Source == nullptr) {
        *_Destination = '\0';
        return 22;
    }
    char* p = _Destination;
    size_t available = _SizeInBytes;
    while (*p != '\0' && available > 0) {
        p++;
        available--;
    }
    if (available == 0) return 80;
    while ((*p++ = *_Source++) != 0 && --available > 0) {}
    if (available == 0) {
        *_Destination = '\0';
        return 80;
    }
    return 0;
}

extern "C" int __cdecl __C_specific_handler_noexcept(
    PEXCEPTION_RECORD ExceptionRecord,
    PVOID EstablisherFrame,
    PCONTEXT ContextRecord,
    PVOID DispatcherContext
) {
    return 1;
}

#define LONGBRIDGE_API extern "C" __declspec(dllexport)

typedef HRESULT(WINAPI* InitUserThemeRegistryProc)();
typedef HRESULT(WINAPI* SetSystemThemeProc)(LPCWSTR pszThemeFileName, LPCWSTR pszColor, DWORD dwFlags);
typedef HRESULT(WINAPI* SetThemeAppPropertiesProc)(DWORD dwFlags);

struct IThemeManager : IUnknown {
    virtual HRESULT STDMETHODCALLTYPE InstallTheme(LPCWSTR pszThemeFileName, DWORD dwFlags) = 0;
};

static const CLSID CLSID_ThemeManager = { 0x0C16D1A0,0xEF3E,0x4AE0,{0x97,0xB3,0x39,0xA7,0x82,0x8F,0x5C,0xE4} };
static const IID IID_IThemeManager = { 0xA7E684B4,0xC920,0x4B8F,{0xB2,0x46,0x1C,0x4B,0x7D,0x34,0x8C,0x2F} };

HMODULE LoadUxTheme() {
    static HMODULE hUxTheme = NULL;
    if (!hUxTheme) {
        hUxTheme = LoadLibraryW(L"uxtheme.dll");
    }
    return hUxTheme;
}

LONGBRIDGE_API HRESULT ThemeTool_Init() {
    HMODULE hUxTheme = LoadUxTheme();
    if (!hUxTheme) return HRESULT_FROM_WIN32(GetLastError());

    auto SetThemeAppProperties = (SetThemeAppPropertiesProc)GetProcAddress(hUxTheme, (LPCSTR)5);
    if (!SetThemeAppProperties) return HRESULT_FROM_WIN32(GetLastError());

    return SetThemeAppProperties(0);
}

LONGBRIDGE_API HRESULT SecureUxTheme_Install(LPCWSTR themePath) {
    HMODULE hUxTheme = LoadUxTheme();
    if (!hUxTheme) return HRESULT_FROM_WIN32(GetLastError());

    auto InitThemeManager = (HRESULT(WINAPI*)())GetProcAddress(hUxTheme, "themetool_init");
    if (!InitThemeManager) return HRESULT_FROM_WIN32(GetLastError());
    HRESULT hr = InitThemeManager();
    if (FAILED(hr)) return hr;

    hr = CoInitializeEx(NULL, COINIT_APARTMENTTHREADED | COINIT_DISABLE_OLE1DDE);
    if (FAILED(hr)) return hr;

    IThemeManager2* pManager = nullptr; // Proper interface declaration
    hr = CoCreateInstance(
        CLSID_ThemeManager2,
        nullptr,
        CLSCTX_LOCAL_SERVER,
        IID_IThemeManager2,
        reinterpret_cast<void**>(&pManager)
    );

    wchar_t fullPath[MAX_PATH] = {0};
    if (SUCCEEDED(hr) && pManager) {
        if (GetFullPathNameW(themePath, MAX_PATH, fullPath, NULL)) {
            hr = pManager->InstallTheme(fullPath, THEMETOOL_APPLY_FLAG_SKIP_SIGNATURE_VALIDATION);
        }
        else {
            hr = HRESULT_FROM_WIN32(GetLastError());
        }
        pManager->Release();
    }

    CoUninitialize();
    return hr;
}

LONGBRIDGE_API HRESULT ThemeTool_SetActive(LPCWSTR themePath) {
    HMODULE hUxTheme = LoadUxTheme();
    if (!hUxTheme) return HRESULT_FROM_WIN32(GetLastError());

    auto SetSystemTheme = (SetSystemThemeProc)GetProcAddress(hUxTheme, (LPCSTR)127);
    if (!SetSystemTheme) return HRESULT_FROM_WIN32(GetLastError());

    return SetSystemTheme(themePath, L"Normal", 0);
}


EXTERN_C __declspec(dllexport) HRESULT themetool_signature_fix(LPCWSTR path)
{
    return themetool_signature_fix(path);
}

BOOL APIENTRY DllMain(HMODULE hModule, DWORD ul_reason_for_call, LPVOID lpReserved) {
    return TRUE;
}