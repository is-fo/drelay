// DrelayPe.cpp -- read-only PE/CLI reads, with no dependency on the runtime.
//
// The layout knowledge encoded here is deliberately narrow: an IMAGE_DOS_HEADER,
// an IMAGE_NT_HEADERS64, the data directory array, and the CLI metadata root.  It
// stops exactly where COR20 stops, because everything past that (the Module
// table) is read through documented CLR metadata offsets rather than by
// reimplementing a metadata reader -- `il_index.py` already is one, and a second
// one in C++ would be a second thing to get wrong.
//
// A NOTE ON THE OFFSETS IN THE METADATA ROOT, because they look arbitrary:
// `DrelayCliHeader` follows the documented fsfix order -- a 72-byte body
// followed by four 8-byte directories -- and the MVID sits at +8 inside the
// Module table row, immediately after the 2-byte Generation and 2-byte Name
// columns, both of which are string-heap indices and so 2 bytes wide.  That
// matches il_index.py's reader, which is the cross-check: if this code and that
// script ever disagree about a build, one of them has the row layout wrong.

#include "DrelayPe.h"
#include "DrelayLog.h"

#include <wincrypt.h>
#include <stdio.h>
#include <string.h>

#pragma comment(lib, "crypt32.lib")

namespace {

// WHY EVERY FAILURE IN THIS FILE NAMES ITS LINE.
//
// These readers return a boolean, and a boolean is not a diagnosis.  The MVID
// reader returned false for two rounds of work against two different assemblies
// while the equivalent parse in `il_index.py` succeeded on both, and the only
// way to tell the twelve possible early returns apart was to rebuild with a
// print.  Recording the line costs one call and turns the next occurrence into
// a location.
void PeFailAt(int line) {
    DrLog(DRLOG_WARN, "PE read failed at DrelayPe.cpp:%d", line);
}

#define DRELAY_COR20_HEADER_SIZE 72u

#pragma pack(push, 1)
struct DrelayCliHeader {
    uint32_t cb;
    uint16_t majorRuntimeVersion;
    uint16_t minorRuntimeVersion;
    uint32_t metaDataRva;
    uint32_t metaDataSize;
    uint32_t flags;
    uint32_t entryPointToken;
    uint32_t resourcesRva;
    uint32_t resourcesSize;
    uint32_t strongNameRva;
    uint32_t strongNameSize;
    uint32_t codeManagerTableRva;
    uint32_t codeManagerTableSize;
    uint32_t vtableFixupsRva;
    uint32_t vtableFixupsSize;
    uint32_t exportAddressTableJumpsRva;
    uint32_t exportAddressTableJumpsSize;
    uint32_t managedNativeHeaderRva;
    uint32_t managedNativeHeaderSize;
};
#pragma pack(pop)

static_assert(sizeof(DrelayCliHeader) == DRELAY_COR20_HEADER_SIZE,
              "CLI header must be the documented 72 bytes");

// Every read below is bounds-checked against `size` even where the input is a
// file we just hashed.  A malformed or truncated image must produce a refusal,
// not a read past the mapping.
template <typename T>
bool ReadAt(const uint8_t* base, size_t size, uint64_t offset, T* out) {
    if (offset > size || size - offset < sizeof(T)) {
        { PeFailAt(__LINE__); return false; }
    }
    memcpy(out, base + offset, sizeof(T));
    return true;
}

struct MappedFile {
    HANDLE file = INVALID_HANDLE_VALUE;
    HANDLE mapping = nullptr;
    const uint8_t* base = nullptr;
    size_t size = 0;

    bool Open(const wchar_t* path) {
        file = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                           nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE) {
            { PeFailAt(__LINE__); return false; } 
        }
        LARGE_INTEGER li;
        if (!GetFileSizeEx(file, &li) || li.QuadPart <= 0) {
            Close();
            { PeFailAt(__LINE__); return false; } 
        }
        size = static_cast<size_t>(li.QuadPart);
        mapping = CreateFileMappingW(file, nullptr, PAGE_READONLY, 0, 0, nullptr);
        if (mapping == nullptr) {
            Close();
            { PeFailAt(__LINE__); return false; } 
        }
        base = static_cast<const uint8_t*>(MapViewOfFile(mapping, FILE_MAP_READ, 0, 0, 0));
        if (base == nullptr) {
            Close();
            { PeFailAt(__LINE__); return false; } 
        }
        return true;
    }

    void Close() {
        if (base != nullptr) {
            UnmapViewOfFile(base);
            base = nullptr;
        }
        if (mapping != nullptr) {
            CloseHandle(mapping);
            mapping = nullptr;
        }
        if (file != INVALID_HANDLE_VALUE) {
            CloseHandle(file);
            file = INVALID_HANDLE_VALUE;
        }
    }

    ~MappedFile() { Close(); }
};

// Locates the CLI header inside a mapped image.  Returns false rather than a
// guess when the image is not a managed PE, which is what makes "the assembly is
// pure IL" checkable from native code.
bool FindCliHeader(const uint8_t* base, size_t size, DrelayCliHeader* out) {
    IMAGE_DOS_HEADER dos = {};
    if (!ReadAt(base, size, 0, &dos) || dos.e_magic != IMAGE_DOS_SIGNATURE) {
        { PeFailAt(__LINE__); return false; }
    }
    if (dos.e_lfanew <= 0) {
        { PeFailAt(__LINE__); return false; }
    }
    IMAGE_NT_HEADERS64 nt = {};
    if (!ReadAt(base, size, static_cast<uint64_t>(dos.e_lfanew), &nt) ||
        nt.Signature != IMAGE_NT_SIGNATURE) {
        { PeFailAt(__LINE__); return false; }
    }
    // A MANAGED ASSEMBLY IS USUALLY PE32, NOT PE32+, AND REQUIRING PE32+ MADE THIS
    // READER FAIL ON EVERY ASSEMBLY IT WAS TRIED AGAINST.
    //
    // `DarzasDominion.dll` and the synthetic host are AnyCPU assemblies, which the
    // CLI emits with `Magic == IMAGE_NT_OPTIONAL_HDR32_MAGIC` even when the apphost
    // beside them is x64 -- the bitness of a managed image is a claim about the IL,
    // not about the machine.  So the check above used to be the whole reason the
    // MVID came back unreadable, and the failure presented as "the identity check is
    // a GAP", which reads like a missing capability rather than a wrong requirement.
    //
    // The optional header differs between the two in exactly one place that matters
    // here -- the data directory starts at +96 for PE32 and +112 for PE32+ -- while
    // `NumberOfRvaAndSizes` is at +92 in both.  `nt.FileHeader` is still used below
    // because IMAGE_NT_HEADERS32 and IMAGE_NT_HEADERS64 begin with the same
    // signature and file header.
    const uint64_t optAt = static_cast<uint64_t>(dos.e_lfanew) + sizeof(uint32_t) +
                           sizeof(IMAGE_FILE_HEADER);
    uint16_t magic = 0;
    if (!ReadAt(base, size, optAt, &magic)) {
        { PeFailAt(__LINE__); return false; }
    }
    uint64_t ddAt = 0;
    // `NumberOfRvaAndSizes` IS NOT AT +92 IN A PE32+ IMAGE, AND READING IT THERE IS
    // WHY THIS READER REPORTED "PE read failed at DrelayPe.cpp:181" ON THE GAME.
    //
    // The header layout differs between the two formats in exactly one extra field
    // ahead of the count: PE32+ carries `ULONGLONG BaseOfData`-shaped 64-bit
    // `ImageBase`, `SizeOfStackReserve`, `SizeOfStackCommit`, `SizeOfHeapReserve`
    // and `SizeOfHeapCommit` where PE32 carries 32-bit ones, so the count sits eight
    // bytes later.  In the wild: `IMAGE_OPTIONAL_HEADER32.NumberOfRvaAndSizes` is at
    // +92, `IMAGE_OPTIONAL_HEADER64.NumberOfRvaAndSizes` is at +108.
    //
    // The previous version read +92 for both, so on the game -- a PE32+ image with
    // `NumberOfSections == 2`, `magic == 0x20B`, 16 directories and a populated COM
    // directory at data-directory entry 14 -- it read zero, took the `<= 16` branch
    // and refused the file.  Measured on the installed build:
    //
    //     u32 @ optAt+92  = 0x00000000     (wrong field: padding/ImageBase low half)
    //     u32 @ optAt+108 = 0x00000010     (16, the real count)
    //     dataDir[14]     = rva 0x2000, size 0x48
    //
    // The moral is the same one this file's header already draws about the MVID: a
    // duplicated layout is a layout that drifts, and the fix is to branch on the
    // magic the way the data-directory offset already did rather than to restate a
    // number that is only correct for one of the two formats.
    uint64_t rvaCountAt = 0;
    if (magic == IMAGE_NT_OPTIONAL_HDR64_MAGIC) {
        ddAt = optAt + 112;
        rvaCountAt = optAt + 108;
    } else if (magic == IMAGE_NT_OPTIONAL_HDR32_MAGIC) {
        ddAt = optAt + 96;
        rvaCountAt = optAt + 92;
    } else {
        { PeFailAt(__LINE__); return false; }
    }
    uint32_t dirCount = 0;
    if (!ReadAt(base, size, rvaCountAt, &dirCount)) {
        { PeFailAt(__LINE__); return false; }
    }
    if (dirCount <= IMAGE_DIRECTORY_ENTRY_COM_DESCRIPTOR) {
        { PeFailAt(__LINE__); return false; }
    }
    const uint64_t comDirAt = ddAt + IMAGE_DIRECTORY_ENTRY_COM_DESCRIPTOR * 8ull;
    uint32_t comRva = 0;
    uint32_t comSize = 0;
    if (!ReadAt(base, size, comDirAt, &comRva) || !ReadAt(base, size, comDirAt + 4, &comSize)) {
        { PeFailAt(__LINE__); return false; }
    }
    (void)comSize;
    if (comRva == 0) {
        { PeFailAt(__LINE__); return false; }
    }
    // A file-mapped image is laid out at its own file offsets, and for a
    // section-aligned managed assembly the CLI header's RVA maps to the same
    // offset only when the section's VirtualAddress equals its PointerToRawData.
    // Rather than assume that, walk the section table.
    uint64_t hdrEnd = static_cast<uint64_t>(dos.e_lfanew) + sizeof(uint32_t) + sizeof(IMAGE_FILE_HEADER) +
                      nt.FileHeader.SizeOfOptionalHeader;
    for (uint32_t i = 0; i < nt.FileHeader.NumberOfSections; ++i) {
        IMAGE_SECTION_HEADER sec = {};
        if (!ReadAt(base, size, hdrEnd + static_cast<uint64_t>(i) * sizeof(IMAGE_SECTION_HEADER), &sec)) {
            { PeFailAt(__LINE__); return false; } 
        }
        const uint64_t span = (sec.Misc.VirtualSize > sec.SizeOfRawData) ? sec.Misc.VirtualSize
                                                                        : sec.SizeOfRawData;
        if (comRva >= sec.VirtualAddress && comRva < sec.VirtualAddress + span) {
            const uint64_t fileOffset = static_cast<uint64_t>(sec.PointerToRawData) + (comRva - sec.VirtualAddress);
            return ReadAt(base, size, fileOffset, out) && out->cb == DRELAY_COR20_HEADER_SIZE;
        }
    }
    { PeFailAt(__LINE__); return false; } 
}

bool ReadMetadataRoot(const uint8_t* base, size_t size, const DrelayCliHeader& cli,
                      const uint8_t** root, size_t* rootSize) {
    if (cli.metaDataRva == 0 || cli.metaDataSize < 20) {
        { PeFailAt(__LINE__); return false; }
    }
    // The metadata directory's RVA is in the same section as the CLI header in
    // practice; resolve it the same way rather than assuming the identity map.
    IMAGE_DOS_HEADER dos = {};
    IMAGE_NT_HEADERS64 nt = {};
    if (!ReadAt(base, size, 0, &dos) ||
        !ReadAt(base, size, static_cast<uint64_t>(dos.e_lfanew), &nt)) {
        { PeFailAt(__LINE__); return false; }
    }
    uint64_t hdrEnd = static_cast<uint64_t>(dos.e_lfanew) + sizeof(uint32_t) + sizeof(IMAGE_FILE_HEADER) +
                      nt.FileHeader.SizeOfOptionalHeader;
    for (uint32_t i = 0; i < nt.FileHeader.NumberOfSections; ++i) {
        IMAGE_SECTION_HEADER sec = {};
        if (!ReadAt(base, size, hdrEnd + static_cast<uint64_t>(i) * sizeof(IMAGE_SECTION_HEADER), &sec)) {
            { PeFailAt(__LINE__); return false; } 
        }
        const uint64_t span = (sec.Misc.VirtualSize > sec.SizeOfRawData) ? sec.Misc.VirtualSize
                                                                        : sec.SizeOfRawData;
        if (cli.metaDataRva >= sec.VirtualAddress && cli.metaDataRva < sec.VirtualAddress + span) {
            const uint64_t off = static_cast<uint64_t>(sec.PointerToRawData) + (cli.metaDataRva - sec.VirtualAddress);
            if (off > size || size - off < cli.metaDataSize) {
                { PeFailAt(__LINE__); return false; } 
            }
            *root = base + off;
            *rootSize = cli.metaDataSize;
            return true;
        }
    }
    { PeFailAt(__LINE__); return false; } 
}

} // namespace

bool DrPeReadMvid(const wchar_t* path, uint8_t* mvidOut) {
    if (path == nullptr || mvidOut == nullptr) {
        { PeFailAt(__LINE__); return false; }
    }
    MappedFile f;
    if (!f.Open(path)) {
        { PeFailAt(__LINE__); return false; }
    }

    DrelayCliHeader cli = {};
    if (!FindCliHeader(f.base, f.size, &cli)) {
        { PeFailAt(__LINE__); return false; }
    }
    const uint8_t* root = nullptr;
    size_t rootSize = 0;
    if (!ReadMetadataRoot(f.base, f.size, cli, &root, &rootSize)) {
        { PeFailAt(__LINE__); return false; }
    }

    uint32_t signature = 0;
    uint32_t versionLen = 0;
    if (!ReadAt(root, rootSize, 0, &signature) || signature != 0x424A5342u) {
        { PeFailAt(__LINE__); return false; } // "BSJB"
    }
    if (!ReadAt(root, rootSize, 12, &versionLen)) {
        { PeFailAt(__LINE__); return false; }
    }
    uint64_t p = 16ull + versionLen;
    uint16_t flags = 0;
    uint16_t streamCount = 0;
    if (!ReadAt(root, rootSize, p, &flags) || !ReadAt(root, rootSize, p + 2, &streamCount)) {
        { PeFailAt(__LINE__); return false; }
    }
    p += 4;

    // THE MVID IS NOT IN THE TABLES STREAM, AND READING IT AS IF IT WERE IS THE
    // BUG THIS REPLACES.
    //
    // The previous version copied 16 bytes starting at `#~` + 24 + 4 -- i.e. just
    // past the Module table row's Generation and Name columns -- and called that
    // the MVID.  But the Module row's `Mvid` column is a *heap index* (2 or 4
    // bytes wide) that points into the `#GUID` stream, not the 16-byte GUID
    // itself.  Against the real game that read `96 00 00 00 ...`, which is the
    // index 150 followed by the EncId and EncBaseId columns and the first bytes of
    // table 1 -- a plausible-looking 16 bytes that is not a GUID at all, so the
    // native identity check could never agree with the runtime.
    //
    // Both stream pointers are collected in one pass because their order in the
    // header is not fixed: `#GUID` can precede `#~`.
    const uint8_t* tables = nullptr;
    size_t tablesSize = 0;
    const uint8_t* guidHeap = nullptr;
    size_t guidHeapSize = 0;

    for (uint16_t i = 0; i < streamCount; ++i) {
        uint32_t offset = 0;
        uint32_t size = 0;
        if (!ReadAt(root, rootSize, p, &offset) || !ReadAt(root, rootSize, p + 4, &size)) {
            { PeFailAt(__LINE__); return false; } 
        }
        char name[32] = {};
        for (size_t c = 0; c < sizeof(name) - 1; ++c) {
            if (!ReadAt(root, rootSize, p + 8 + c, &name[c])) {
                { PeFailAt(__LINE__); return false; } 
            }
            if (name[c] == '\0') {
                break;
            }
        }
        // Stream headers are DWORD-aligned, and the name is NUL-terminated and
        // padded to a 4-byte boundary.
        const uint64_t nameLen = strlen(name) + 1;
        p += 8 + ((nameLen + 3) & ~3ull);
        // A STREAM THAT DOES NOT FIT IS SKIPPED, NOT A FAILURE.
        //
        // The first version of this returned false here, copying the check that
        // the older code applied only to `#~`.  That made the whole read fail on
        // both assemblies it was tried against -- the 5 KB synthetic host and the
        // 50 MB game -- while the older, wrong-offset version at least opened
        // them.  A stream header whose extent runs past the metadata root is a
        // real thing to meet (a trailer or an empty stream at the very end), and
        // the two streams that matter are checked for their own extent below.
        if (offset > rootSize || rootSize - offset < size) {
            continue;
        }
        if (strcmp(name, "#~") == 0 || strcmp(name, "#-") == 0) {
            tables = root + offset;
            tablesSize = size;
        } else if (strcmp(name, "#GUID") == 0) {
            guidHeap = root + offset;
            guidHeapSize = size;
        }
    }

    if (tables == nullptr || guidHeap == nullptr) {
        { PeFailAt(__LINE__); return false; } // a managed image always has both
    }

    // The tables stream's own header is 24 bytes: reserved(4), major(1),
    // minor(1), heapSizes(1), reserved2(1), valid(8), sorted(8).
    //
    // THEN COME THE ROW COUNTS, AND FORGETTING THEM IS WHY THIS READER FAILED.
    // After the header the stream carries a 4-byte row count for *every* table
    // whose bit is set in `valid`, in table-index order, and only then the rows
    // themselves.  Reading the Module row straight after the 24-byte header lands
    // on the Module table's own row count -- so `Generation` came back as 1, `Name`
    // as 0, and the `Mvid` column as 23, an index into a GUID heap that holds one
    // entry.  The row counts are not a documented layout in the CLI header the way
    // COR20 is, and `il_index.py` (`p = t + 24 + 4 * bin(valid).count("1")`) is
    // where this was finally read off; that script has parsed this build since
    // Phase 0 and is the cross-check this file's header comment claims.
    uint8_t heapSizes = 0;
    uint64_t valid = 0;
    if (!ReadAt(tables, tablesSize, 6, &heapSizes) || !ReadAt(tables, tablesSize, 8, &valid)) {
        { PeFailAt(__LINE__); return false; }
    }
    const bool wideString = (heapSizes & 0x01u) != 0;
    const bool wideGuid = (heapSizes & 0x02u) != 0;

    uint32_t presentTables = 0;
    for (uint32_t bit = 0; bit < 64; ++bit) {
        if ((valid >> bit) & 1ull) {
            ++presentTables;
        }
    }
    const uint64_t rowsAt = 24ull + 4ull * presentTables;

    // Module is table 0.  Its row is: Generation (2 bytes), Name (a string-heap
    // index), then Mvid (a GUID-heap index).  Each index is 2 bytes unless the
    // corresponding bit in heapSizes widens it to 4.
    const uint64_t nameAt = rowsAt + 2;
    const uint64_t mvidAt = nameAt + (wideString ? 4ull : 2ull);

    uint32_t mvidIndex = 0;
    if (wideGuid) {
        if (!ReadAt(tables, tablesSize, mvidAt, &mvidIndex)) {
            { PeFailAt(__LINE__); return false; } 
        }
    } else {
        uint16_t narrow = 0;
        if (!ReadAt(tables, tablesSize, mvidAt, &narrow)) {
            { PeFailAt(__LINE__); return false; } 
        }
        mvidIndex = narrow;
    }

    // The GUID heap is 1-based and every entry is exactly 16 bytes.
    if (mvidIndex == 0) {
        { PeFailAt(__LINE__); return false; } // the Module row has no MVID, which a real assembly never has
    }
    const uint64_t guidAt = static_cast<uint64_t>(mvidIndex - 1) * 16ull;
    if (guidAt > guidHeapSize || guidHeapSize - guidAt < 16) {
        { PeFailAt(__LINE__); return false; }
    }
    memcpy(mvidOut, guidHeap + guidAt, 16);
    return true;
}

bool DrPeHashFile(const wchar_t* path, uint8_t* sha256Out, int64_t* sizeOut) {
    if (path == nullptr || sha256Out == nullptr) {
        { PeFailAt(__LINE__); return false; }
    }

    HANDLE file = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr,
                              OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL | FILE_FLAG_SEQUENTIAL_SCAN, nullptr);
    if (file == INVALID_HANDLE_VALUE) {
        { PeFailAt(__LINE__); return false; }
    }

    HCRYPTPROV prov = 0;
    HCRYPTHASH hash = 0;
    bool ok = false;
    if (CryptAcquireContextW(&prov, nullptr, nullptr, PROV_RSA_AES, CRYPT_VERIFYCONTEXT)) {
        if (CryptCreateHash(prov, CALG_SHA_256, 0, 0, &hash)) {
            // 1 MiB chunks: large enough that the 50 MB assembly is a few dozen
            // reads, small enough to stay off the large-page path.
            static const DWORD kChunk = 1024 * 1024;
            BYTE* buffer = static_cast<BYTE*>(HeapAlloc(GetProcessHeap(), 0, kChunk));
            if (buffer != nullptr) {
                ok = true;
                int64_t total = 0;
                for (;;) {
                    DWORD got = 0;
                    if (!ReadFile(file, buffer, kChunk, &got, nullptr)) {
                        ok = false;
                        break;
                    }
                    if (got == 0) {
                        break;
                    }
                    total += got;
                    if (!CryptHashData(hash, buffer, got, 0)) {
                        ok = false;
                        break;
                    }
                }
                if (ok) {
                    BYTE digest[32] = {};
                    DWORD digestLen = sizeof(digest);
                    if (CryptGetHashParam(hash, HP_HASHVAL, digest, &digestLen, 0)) {
                        memcpy(sha256Out, digest, 32);
                        if (sizeOut != nullptr) {
                            *sizeOut = total;
                        }
                    } else {
                        ok = false;
                    }
                }
                HeapFree(GetProcessHeap(), 0, buffer);
            }
            CryptDestroyHash(hash);
        }
        CryptReleaseContext(prov, 0);
    }
    CloseHandle(file);
    return ok;
}

bool DrPeAddressInSection(const void* address, const char* sectionName) {
    if (address == nullptr || sectionName == nullptr) {
        { PeFailAt(__LINE__); return false; }
    }
    HMODULE module = nullptr;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                            GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                            reinterpret_cast<LPCWSTR>(address), &module)) {
        { PeFailAt(__LINE__); return false; }
    }
    const uint8_t* base = reinterpret_cast<const uint8_t*>(module);
    const IMAGE_DOS_HEADER* dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(base);
    if (dos->e_magic != IMAGE_DOS_SIGNATURE) {
        { PeFailAt(__LINE__); return false; }
    }
    const IMAGE_NT_HEADERS64* nt = reinterpret_cast<const IMAGE_NT_HEADERS64*>(base + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE || nt->OptionalHeader.Magic != IMAGE_NT_OPTIONAL_HDR64_MAGIC) {
        { PeFailAt(__LINE__); return false; }
    }
    const uint64_t rva = reinterpret_cast<const uint8_t*>(address) - base;
    const IMAGE_SECTION_HEADER* sec = IMAGE_FIRST_SECTION(nt);
    for (uint32_t i = 0; i < nt->FileHeader.NumberOfSections; ++i, ++sec) {
        char name[9] = {};
        memcpy(name, sec->Name, 8);
        const uint64_t span = (sec->Misc.VirtualSize > sec->SizeOfRawData) ? sec->Misc.VirtualSize
                                                                          : sec->SizeOfRawData;
        if (rva >= sec->VirtualAddress && rva < sec->VirtualAddress + span) {
            return strcmp(name, sectionName) == 0;
        }
    }
    { PeFailAt(__LINE__); return false; } 
}

uint32_t DrPeImageBaseOf(const void* address) {
    HMODULE module = nullptr;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                            GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                            reinterpret_cast<LPCWSTR>(address), &module)) {
        return 0;
    }
    const uint8_t* base = reinterpret_cast<const uint8_t*>(module);
    const IMAGE_DOS_HEADER* dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(base);
    if (dos->e_magic != IMAGE_DOS_SIGNATURE) {
        return 0;
    }
    return static_cast<uint32_t>(reinterpret_cast<uintptr_t>(base));
}

bool DrPeModuleNameOf(const void* address, wchar_t* out, size_t capChars) {
    if (out == nullptr || capChars == 0) {
        { PeFailAt(__LINE__); return false; }
    }
    HMODULE module = nullptr;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                            GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                            reinterpret_cast<LPCWSTR>(address), &module)) {
        { PeFailAt(__LINE__); return false; }
    }
    return GetModuleFileNameW(module, out, static_cast<DWORD>(capChars)) != 0;
}

uint32_t DrPeEntryPointToken(const wchar_t* path) {
    if (path == nullptr) {
        return 0;
    }
    MappedFile f;
    if (!f.Open(path)) {
        return 0;
    }
    DrelayCliHeader cli = {};
    if (!FindCliHeader(f.base, f.size, &cli)) {
        return 0;
    }
    // The native-entry RVA field overlaps the token on purpose in COR20: for a
    // managed assembly the entry point is a token, and the union is documented.
    return cli.entryPointToken;
}
