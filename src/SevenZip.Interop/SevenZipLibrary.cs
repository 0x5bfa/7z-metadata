using System.Runtime.InteropServices;

namespace SevenZip.Interop.Runtime;

/// <summary>Loads the bundled 7-Zip DLL and creates objects through its exported factory.</summary>
public sealed class SevenZipLibrary
{
    private static readonly Lazy<SevenZipLibrary> InstanceValue = new(LoadCore);

    private readonly CreateObjectDelegate _createObject;

    private SevenZipLibrary(CreateObjectDelegate createObject)
    {
        _createObject = createObject;
    }

    /// <summary>Gets the process-wide loader for the current Windows runtime architecture.</summary>
    public static SevenZipLibrary Instance => InstanceValue.Value;

    /// <summary>
    /// Creates a 7-Zip COM-style object for the specified class and interface identifiers.
    /// </summary>
    /// <param name="classId">The format or codec class identifier from the 7-Zip metadata.</param>
    /// <param name="interfaceId">The requested interface identifier.</param>
    /// <returns>An owned interface pointer. Dispose it to call Release.</returns>
    /// <exception cref="COMException">The 7-Zip factory returned a failing HRESULT.</exception>
    public SevenZipObject CreateObject(Guid classId, Guid interfaceId)
    {
        int result = _createObject(ref classId, ref interfaceId, out nint instance);
        Marshal.ThrowExceptionForHR(result);

        if (instance == 0)
        {
            throw new InvalidOperationException("7-Zip returned a null interface pointer.");
        }

        return new SevenZipObject(instance);
    }

    private static SevenZipLibrary LoadCore()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The bundled 7-Zip DLL is available only on Windows.");
        }

        nint module = NativeLibrary.Load(
            "7z.dll",
            typeof(SevenZipLibrary).Assembly,
            DllImportSearchPath.SafeDirectories);

        try
        {
            nint entryPoint = NativeLibrary.GetExport(module, "CreateObject");
            CreateObjectDelegate createObject = Marshal.GetDelegateForFunctionPointer<CreateObjectDelegate>(entryPoint);
            return new SevenZipLibrary(createObject);
        }
        catch
        {
            NativeLibrary.Free(module);
            throw;
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int CreateObjectDelegate(ref Guid classId, ref Guid interfaceId, out nint instance);
}

/// <summary>Owns a 7-Zip interface pointer and releases it when disposed.</summary>
public sealed class SevenZipObject : IDisposable
{
    private nint _instance;

    internal SevenZipObject(nint instance)
    {
        _instance = instance;
    }

    /// <summary>Gets the raw interface pointer for use with the matching WinMD ABI type.</summary>
    /// <exception cref="ObjectDisposedException">The object has already been released.</exception>
    public nint DangerousGetHandle()
    {
        nint instance = Volatile.Read(ref _instance);
        return instance != 0 ? instance : throw new ObjectDisposedException(nameof(SevenZipObject));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Release();
        GC.SuppressFinalize(this);
    }

    /// <summary>Releases the owned 7-Zip interface pointer.</summary>
    ~SevenZipObject()
    {
        Release();
    }

    private void Release()
    {
        nint instance = Interlocked.Exchange(ref _instance, 0);
        if (instance == 0)
        {
            return;
        }

        nint vtable = Marshal.ReadIntPtr(instance);
        nint releaseEntryPoint = Marshal.ReadIntPtr(vtable, IntPtr.Size * 2);
        ReleaseDelegate release = Marshal.GetDelegateForFunctionPointer<ReleaseDelegate>(releaseEntryPoint);
        _ = release(instance);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint ReleaseDelegate(nint instance);
}
