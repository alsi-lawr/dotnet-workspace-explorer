namespace Dotnet.WorkspaceExplorer.WorkspaceEditing

open System
open System.Runtime.InteropServices

module private MacArtifactTrashInterop =
    [<DllImport("/usr/lib/libobjc.A.dylib")>]
    extern IntPtr objc_getClass(string _name)

    [<DllImport("/usr/lib/libobjc.A.dylib")>]
    extern IntPtr sel_registerName(string _name)

    [<DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")>]
    extern IntPtr send0(IntPtr _receiver, IntPtr _selector)

    [<DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")>]
    extern IntPtr sendUtf8(
        IntPtr _receiver,
        IntPtr _selector,
        [<MarshalAs(UnmanagedType.LPUTF8Str)>] string _value
    )

    [<DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")>]
    extern IntPtr sendPointer(IntPtr _receiver, IntPtr _selector, IntPtr _value)

    [<DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")>]
    extern byte sendTrash(
        IntPtr _receiver,
        IntPtr _selector,
        IntPtr _url,
        IntPtr _result,
        IntPtr _error
    )

    let selector value = sel_registerName value

    let trash path =
        let manager = send0 (objc_getClass "NSFileManager", selector "defaultManager")

        let text =
            sendUtf8 (objc_getClass "NSString", selector "stringWithUTF8String:", path)

        let url = sendPointer (objc_getClass "NSURL", selector "fileURLWithPath:", text)

        sendTrash (
            manager,
            selector "trashItemAtURL:resultingItemURL:error:",
            url,
            IntPtr.Zero,
            IntPtr.Zero
        )
        <> 0uy

type internal MacArtifactTrash() =
    interface ArtifactTrash with
        member _.MoveToTrash path =
            try
                if MacArtifactTrashInterop.trash path then
                    Ok()
                else
                    Error { Message = "The native macOS trash API refused the item." }
            with ex ->
                Error { Message = ex.Message }
