// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.Diagnostics.DataContractReader;

public enum DataType
{
    Unknown = 0,

    int8,
    uint8,
    int16,
    uint16,
    int32,
    uint32,
    int64,
    uint64,
    nint,
    nuint,
    pointer,

    /* VM Data Types */

    ObjectHandle,
    JITNotification,
    CodePointer,
    Thread,
    ThreadStore,
    ThreadLocalData,
    ThreadStaticsInfo,
    InFlightTLSData,
    TLSIndex,
    GCAllocContext,
    EEAllocContext,
    [ManagedType("System.Exception")]
    Exception,
    ExceptionInfo,
    EEExceptionClause,
    ExceptionLookupTableEntry,
    EEILException,
    R2RExceptionClause,
    RuntimeThreadLocals,
    IdDispenser,
    Module,
    ModuleLookupMap,
    AppDomain,
    Debugger,
    DebuggerRCThread,
    SystemDomain,
    Assembly,
    LoaderAllocator,
    LoaderHeap,
    LoaderHeapBlock,
    PEAssembly,
    AssemblyBinder,
    PEImage,
    PEImageLayout,
    WebcilHeader,
    WebcilSectionHeader,
    CGrowableSymbolStream,
    ProbeExtensionResult,
    MethodTable,
    DynamicStaticsInfo,
    EEClass,
    CoreLibBinder,
    MethodTableAuxiliaryData,
    GenericsDictInfo,
    TypeDesc,
    ParamTypeDesc,
    TypeVarTypeDesc,
    FnPtrTypeDesc,
    FieldDesc,
    DynamicMetadata,
    StressLog,
    StressLogModuleDesc,
    StressLogHeader,
    ThreadStressLog,
    StressLogChunk,
    StressMsg,
    StressMsgHeader,
    Object,
    NativeObjectWrapperObject,
    ManagedObjectWrapperHolderObject,
    ManagedObjectWrapperLayout,
    ComWrappersVtablePtrs,
    [ManagedType("System.String")]
    String,
    MethodDesc,
    MethodDescChunk,
    MethodDescCodeData,
    PlatformMetadata,
    PrecodeMachineDescriptor,
    StubPrecodeData,
    FixupPrecodeData,
    ThisPtrRetBufPrecodeData,
    Array,
    SyncBlock,
    SyncTableEntry,
    ObjectHeader,
    InteropSyncBlockInfo,
    SyncBlockCache,
    InstantiatedMethodDesc,
    DynamicMethodDesc,
    StoredSigMethodDesc,
    ArrayMethodDesc,
    FCallMethodDesc,
    PInvokeMethodDesc,
    EEImplMethodDesc,
    CLRToCOMCallMethodDesc,
    RangeSectionMap,
    RangeSectionFragment,
    RangeSection,
    RealCodeHeader,
    CodeHeapListNode,
    CodeHeap,
    LoaderCodeHeap,
    HostCodeHeap,
    MethodDescVersioningState,
    ILCodeVersioningState,
    NativeCodeVersionNode,
    ProfControlBlock,
    ILCodeVersionNode,
    ReadyToRunInfo,
    ReadyToRunHeader,
    ReadyToRunSection,
    ReadyToRunCoreHeader,
    ReadyToRunCoreInfo,
    ImageDataDirectory,
    RuntimeFunction,
    HashMap,
    Bucket,
    UnwindInfo,
    UnwindCode,
    NonVtableSlot,
    MethodImpl,
    NativeCodeSlot,
    AsyncMethodData,
    GCCoverageInfo,
    ArrayListBase,
    ArrayListBlock,
    EETypeHashTable,
    InstMethodHashTable,
    DynamicILBlobTable,
    EEJitManager,
    PatchpointInfo,
    PortableEntryPoint,
    VirtualCallStubManager,
    EEConfig,

    TransitionBlock,
    DebuggerEval,
    ArgumentRegisters,
    CalleeSavedRegisters,
    HijackArgs,

    Frame,
    InlinedCallFrame,
    SoftwareExceptionFrame,
    FramedMethodFrame,
    FuncEvalFrame,
    ResumableFrame,
    FaultingExceptionFrame,
    HijackFrame,
    TailCallFrame,
    StubDispatchFrame,
    ComCallWrapper,
    SimpleComCallWrapper,
    ComMethodTable,
    RCWCleanupList,
    RCW,
    CtxEntry,
    InterfaceEntry,
    ComInterfaceEntry,
    InternalComInterfaceDispatch,
    AuxiliarySymbolInfo,

    /* GC Data Types */

    GCHeap,
    Generation,
    CFinalize,
    HeapSegment,
    OomHistory,
    HandleTableMap,
    HandleTableBucket,
    HandleTable,
    TableSegment,
    CardTableInfo,
    RegionFreeList,

    /*
     * Managed-only well-known types.
     *
     * These are not present in the native data descriptor (datadescriptor.inc); layout for
     * them is resolved via <see cref="ITypeInfoSource"/> implementations (today, the
     * <c>MetadataLayoutSource</c> contract, which reads ECMA metadata from the system
     * assembly). The <see cref="ManagedTypeAttribute"/> names the fully-qualified managed
     * type the source should look up.
     *
     * Offsets returned by <c>MetadataLayoutSource</c> are pre-shifted by
     * <c>sizeof(Object)</c> so callers can compute <c>objectAddress + field.Offset</c>
     * directly (matching the convention used by descriptor-provided reference-type layouts).
     */

    [ManagedType("System.Threading.Lock")]
    Lock,

    [ManagedType("System.Collections.Generic.List`1")]
    List,

    [ManagedType("System.Runtime.CompilerServices.ConditionalWeakTable`2")]
    ConditionalWeakTable,

    [ManagedType("System.Runtime.CompilerServices.ConditionalWeakTable`2+Container")]
    ConditionalWeakTableContainer,

    [ManagedType("System.Runtime.CompilerServices.ConditionalWeakTable`2+Entry")]
    ConditionalWeakTableEntry,

    [ManagedType("System.Runtime.InteropServices.ComWrappers")]
    ComWrappers,

    [ManagedType("System.Runtime.InteropServices.ComWrappers+NativeObjectWrapper")]
    NativeObjectWrapper,

    [ManagedType("System.Runtime.InteropServices.ComWrappers+ManagedObjectWrapperHolder")]
    ManagedObjectWrapperHolder,
}
