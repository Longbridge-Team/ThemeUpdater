#pragma once
#include <Windows.h>
#include <objbase.h>

#define LONGBRIDGE_API extern "C" __declspec(dllexport)
#define THEMETOOL_APPLY_FLAG_SKIP_SIGNATURE_VALIDATION 0x01

struct IThemeManager2 : IUnknown {
    virtual HRESULT STDMETHODCALLTYPE InstallTheme(LPCWSTR, DWORD) = 0;
};

// GUID declarations matching SecureUxTheme's ThemeLib implementation
__declspec(selectany) extern const CLSID CLSID_ThemeManager2 = 
    {0x9324da94,0x50ec,0x4a14,{0xa7,0x70,0xe9,0x0c,0xa0,0x3e,0x7c,0x8f}};

__declspec(selectany) extern const IID IID_IThemeManager2 = 
    {0xc1e8c83e,0x845d,0x4d95,{0x81,0xdb,0xe2,0x83,0xfd,0xff,0xc0,0x00}};

LONGBRIDGE_API HRESULT ThemeTool_Init();
LONGBRIDGE_API HRESULT SecureUxTheme_Install(LPCWSTR themePath);
LONGBRIDGE_API HRESULT ThemeTool_SetActive(LPCWSTR themePath);