#include "stdafx.h"

#include "guiresource.h"

void ExtractResource(TCHAR *path, HRSRC resource) {
	HMODULE hModule = GetModuleHandle(NULL);
	HGLOBAL hGlobal = LoadResource(hModule, resource);
	DWORD size = SizeofResource(hModule, resource);
	void *data = LockResource(hGlobal);
	HANDLE hFile = CreateFile(path, GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
	DWORD writtenBytes;
	WriteFile(hFile, data, size, &writtenBytes, NULL);
	CloseHandle(hFile);
}