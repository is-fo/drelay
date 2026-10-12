// DrelayJit.h -- just enough of the JIT/EE interface to detour the JIT safely.
//
// SCOPE, DELIBERATELY SMALL
// -------------------------
// The hook is not a JIT inspector.  It needs exactly three things from this
// interface: the vtable slot that compiles a method, the vtable slot that proves
// which runtime we are in, and one slot that turns a method handle into a
// metadata token for *diagnostic* purposes.  Everything else is here only
// because it is before those three in the vtable, and a vtable index is a count
// of everything that precedes it -- so the declarations below are load-bearing
// even where they are never called, and removing an unused one silently moves
// every index after it.
//
// That is also why the order matters more than the contents.  The canonical
// ordering is `src/coreclr/inc/icorjitinfoimpl_generated.h`, which the runtime's
// own generator emits "in exactly the same order, with exactly the same
// whitespace, as the ICorJitInfo interface declaration".  `tools/jit_slots.py`
// parses that file and prints the indices; the constants below are that tool's
// output for .NET 9, and DrelayInstall records which slot it actually used at
// runtime rather than assuming these are right.
//
// THE ONE THING THAT IS VERIFIED AND MUST NOT BE "SIMPLIFIED"
// ----------------------------------------------------------
// `getMethodDefFromMethod` is at 0x7A, NOT the 0x76 that MonoMod's generated
// header documents, and not the 105 that xoofx/ManagedJit uses.  0x76 is
// `runWithErrorTrap` on .NET 9.  Calling the wrong slot with a method handle
// does not fail -- it calls an unrelated function with an unrelated signature,
// which is exactly the class of mistake the architecture document warns about.
// The runtime proof of the index is the token it returns (see spike 1.2).

#ifndef DRELAY_JIT_H
#define DRELAY_JIT_H

#include <windows.h>
#include <stdint.h>
#include <stddef.h>   // offsetof, for the static_asserts on the two structs below

// The JIT/EE interface version identifier for .NET 9, from
// src/coreclr/inc/jiteeversionguid.h at release/9.0.  Reading this back through
// vtbl[0x02] is the positive proof that we are holding the JIT object and not
// some other four-slot vtable that happened to be at the same address.
#define DRELAY_JITEE_GUID_DOTNET9 { 0xd6218a78, 0x9a34, 0x4c6f, \
    { 0x8d, 0xb5, 0x07, 0x7a, 0x06, 0x02, 0x2f, 0xae } }

// ICorJitCompiler occupies slots 0..3; compileMethod and getVersionIdentifier
// are the two the hook uses.
#define DRELAY_VTBL_COMPILE_METHOD        0x00u
#define DRELAY_VTBL_GET_VERSION_ID        0x02u
#define DRELAY_VTBL_ICOMPILER_SLOTS       4u
#define DRELAY_VTBL_TOTAL_SLOTS_DOTNET9   178u

// ICorJitInfo slots the hook cares about, as derived by tools/jit_slots.py.
// `icos` = the slot is in ICorStaticInfo's contribution; listed here so a future
// reader can re-derive rather than re-guess.
#define DRELAY_VTBL_GET_METHOD_DEF        0x7Au  // <-- NOT 0x76
#define DRELAY_VTBL_GET_FIELD_OFFSET      0x62u
#define DRELAY_VTBL_IS_FIELD_STATIC       0x67u
#define DRELAY_VTBL_GET_STATIC_BASE       0x33u
#define DRELAY_VTBL_GET_CLASS_NUM_FIELDS  0x39u
#define DRELAY_VTBL_GET_FIELD_IN_CLASS    0x3Au

// Community values that are wrong, retained as data so the spike can *test* them
// rather than merely disbelieve them.  Testing a wrong index is how we find out
// what it really points at; 0x76 is expected to behave like `runWithErrorTrap`.
#define DRELAY_VTBL_GET_METHOD_DEF_MONOMOD  0x76u
#define DRELAY_VTBL_GET_METHOD_DEF_MANAGEDJIT 105u
#define DRELAY_VTBL_GET_FIELD_OFFSET_MONOMOD 0x5Eu

typedef struct DrelayCorJitResult {
    int32_t  value;
} DrelayCorJitResult;

#define DRELAY_CORJIT_OK              0
#define DRELAY_CORJIT_INTERNALERROR   0x80000003

typedef enum DrelayCorInfoType {
    DRELAY_CORINFO_TYPE_UNDEF   = 0,
    DRELAY_CORINFO_TYPE_VOID    = 1,
    DRELAY_CORINFO_TYPE_BOOL    = 2,
    DRELAY_CORINFO_TYPE_CHAR    = 3,
    DRELAY_CORINFO_TYPE_I1      = 4,
    DRELAY_CORINFO_TYPE_U1      = 5,
    DRELAY_CORINFO_TYPE_I2      = 6,
    DRELAY_CORINFO_TYPE_U2      = 7,
    DRELAY_CORINFO_TYPE_I4      = 8,
    DRELAY_CORINFO_TYPE_U4      = 9,
    DRELAY_CORINFO_TYPE_I8      = 10,
    DRELAY_CORINFO_TYPE_U8      = 11,
    DRELAY_CORINFO_TYPE_R4      = 12,
    DRELAY_CORINFO_TYPE_R8      = 13,
    DRELAY_CORINFO_TYPE_STRING  = 14,
    DRELAY_CORINFO_TYPE_PTR     = 15,
    DRELAY_CORINFO_TYPE_BYREF   = 16,
    DRELAY_CORINFO_TYPE_VALUECLASS = 17,
    DRELAY_CORINFO_TYPE_CLASS   = 18,
    DRELAY_CORINFO_TYPE_REFANY  = 20,
    DRELAY_CORINFO_TYPE_VAR     = 24
} DrelayCorInfoType;

typedef enum DrelayCorInfoCallConv {
    DRELAY_CORINFO_CALL_CURRENT      = 0x00,
    DRELAY_CORINFO_CALL_STD          = 0x01,
    DRELAY_CORINFO_CALL_VARARG       = 0x02,
    DRELAY_CORINFO_CALL_UNMANAGED    = 0x03,
    DRELAY_CORINFO_CALL_STD_MASK     = 0x03,
    DRELAY_CORINFO_CALL_HASTHIS      = 0x20,
    DRELAY_CORINFO_CALL_ISVIRTUAL    = 0x40,
    DRELAY_CORINFO_CALL_PRIVATE      = 0x80
} DrelayCorInfoCallConv;

typedef struct DrelayCorInfoSigInst {
    uint32_t classInstCount;
    void*    classInst;
    uint32_t methInstCount;
    void*    methInst;
} DrelayCorInfoSigInst;

// CORINFO_SIG_INFO, COPIED FROM THE RUNTIME'S OWN HEADER RATHER THAN FROM A
// BINDING.
//
// THIS STRUCT WAS WRONG FOR THE WHOLE OF PHASE 1, AND THAT IS THE POINT OF THE
// COMMENT.  The previous version declared `callConv; retType; retTypeClass;
// retTypeSigClass; args; numArgs; token; sigInst; flags; pSig; sigLen;` -- an
// ordering copied from a community binding, with three aggregate members widened
// to pointers and the bitfields expanded into separate fields.  It computed to
// 96 bytes against the runtime's 112, which moved `ILCode` from offset 16 to
// offset 104 and `ILCodeSize` from 24 to 112 *inside CORINFO_METHOD_INFO*.  The
// detour was therefore reading an `mdToken` where it expected a pointer to IL
// and the low half of an argument-list handle where it expected an IL size.
// Nothing about that fails loudly: the size pre-filter simply never matched a
// target, and the handful of times a garbage size did match, `CryptHashData` was
// handed a wild pointer.
//
// The authority is `src/coreclr/inc/corinfo.h` at `release/9.0`, which is the
// header the runtime is compiled from.  The two `static_assert`s below are the
// part that makes this checkable rather than trusted: if a future runtime moves
// a field, the build fails with the offset rather than the hook reading an
// unrelated number.
typedef struct DrelayCorInfoSigInfo {
    int32_t                 callConv;          // CorInfoCallConv, an enum -> 4 bytes
    void*                   retTypeClass;      // @ 8
    void*                   retTypeSigClass;   // @16
    // `CorInfoType retType : 8; unsigned flags : 8; unsigned numArgs : 16;`
    // Three bitfields share ONE four-byte storage unit.  Declaring them as three
    // separate fields is how the previous version gained a pointer-sized hole.
    uint32_t                retTypeFlagsNumArgs;  // @24
    DrelayCorInfoSigInst    sigInst;           // @32, 32 bytes
    void*                   args;              // @64  CORINFO_ARG_LIST_HANDLE
    void*                   pSig;              // @72  PCCOR_SIGNATURE
    uint32_t                cbSig;             // @80
    uint8_t                 pad0[4];
    void*                   methodSignature;   // @88
    void*                   scope;             // @96  CORINFO_MODULE_HANDLE
    uint32_t                token;             // @104 mdToken
    uint8_t                 pad1[4];           //     to 112, the struct's alignment
} DrelayCorInfoSigInfo;

typedef struct DrelayCorMethodInfo {
    void*                    ftn;          // @  0 CORINFO_METHOD_HANDLE
    void*                    scope;        // @  8 CORINFO_MODULE_HANDLE
    uint8_t*                 ILCode;       // @ 16
    uint32_t                 ILCodeSize;   // @ 24
    uint32_t                 maxStack;     // @ 28
    uint32_t                 EHcount;      // @ 32
    uint32_t                 options;      // @ 36 CorInfoOptions, an enum
    uint32_t                 regionKind;   // @ 40 CorInfoRegionKind, an enum
    uint8_t                  pad0[4];
    DrelayCorInfoSigInfo     args;         // @ 48, 112 bytes
    DrelayCorInfoSigInfo     locals;       // @160, 112 bytes
} DrelayCorMethodInfo;

// The contract, checked by the compiler on every build.  `offsetof` is what
// turns "the layout is right" from an assumption into a build step, which is the
// same move `SizeProbe.c` makes for the shared block.
static_assert(sizeof(DrelayCorInfoSigInfo) == 112, "CORINFO_SIG_INFO is 112 bytes on .NET 9 x64");
static_assert(offsetof(DrelayCorInfoSigInfo, retTypeClass) == 8, "CORINFO_SIG_INFO.retTypeClass");
static_assert(offsetof(DrelayCorInfoSigInfo, sigInst) == 32, "CORINFO_SIG_INFO.sigInst");
static_assert(offsetof(DrelayCorInfoSigInfo, args) == 64, "CORINFO_SIG_INFO.args");
static_assert(offsetof(DrelayCorInfoSigInfo, token) == 104, "CORINFO_SIG_INFO.token");
static_assert(sizeof(DrelayCorMethodInfo) == 272, "CORINFO_METHOD_INFO is 272 bytes on .NET 9 x64");
static_assert(offsetof(DrelayCorMethodInfo, ftn) == 0, "CORINFO_METHOD_INFO.ftn");
static_assert(offsetof(DrelayCorMethodInfo, ILCode) == 16, "CORINFO_METHOD_INFO.ILCode");
static_assert(offsetof(DrelayCorMethodInfo, ILCodeSize) == 24, "CORINFO_METHOD_INFO.ILCodeSize");


// Only the eight leading slots are declared, with filler vtable entries for
// slots 8..177.  Declaring them keeps the arithmetic honest: the filler count is
// a visible constant rather than an unexplained pointer arithmetic expression.
#define DRELAY_VTBL_FILLER_SLOTS \
    ((DRELAY_VTBL_TOTAL_SLOTS_DOTNET9 - DRELAY_VTBL_ICOMPILER_SLOTS - 8u))

// Names for the ICorJitCompiler slots the hook touches, so a reader can see the
// calling convention and the exact signature rather than an offset into an array.
typedef struct DrelayICorJitCompilerVtbl {
    DrelayCorJitResult (__cdecl *compileMethod)(
        void* self, void* info, DrelayCorMethodInfo* methodInfo,
        unsigned flags, uint8_t** nativeEntry, uint32_t* nativeSize);
    void (__cdecl *processShutdownWork)(void* self, void* work);
    int  (__cdecl *getVersionIdentifier)(void* self, void* versionIdentifier, uint32_t* identifierSize);
    void (__cdecl *setTargetOS)(void* self, int os);
} DrelayICorJitCompilerVtbl;

// The three ICorJitInfo slots the hook calls, reached by index so the filler
// above does not need a 178-entry struct:
//   getMethodDefFromMethod(self, CORINFO_METHOD_HANDLE, mdMethodDef*)
//   getFieldOffset(self, CORINFO_FIELD_HANDLE) -> unsigned
//   isFieldStatic(self, CORINFO_FIELD_HANDLE) -> bool
typedef uint32_t (__cdecl *DrelayGetMethodDefFn)(void* self, void* ftn, uint32_t* tokenOut);
typedef uint32_t (__cdecl *DrelayGetFieldOffsetFn)(void* self, void* field);
typedef int      (__cdecl *DrelayIsFieldStaticFn)(void* self, void* field);

static __forceinline void* DrelayVtblSlot(void* self, uint32_t index) {
    void*** vptrAtSelf = static_cast<void***>(self);
    void** vtable = *vptrAtSelf;
    return vtable[index];
}

#endif // DRELAY_JIT_H
