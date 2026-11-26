using System.IO;
using System.Runtime.InteropServices;

namespace PhotoTag.Services;

/// <summary>
/// Wybór folderu w zwykłym oknie „Otwórz” systemu Windows (widać zdjęcia w środku) z dodatkowym polem
/// „Wczytaj także zdjęcia z podfolderów” obok przycisku Otwórz (IFileDialogCustomize — tego nie da okno WPF).
/// Folder wybiera się, wchodząc do niego i klikając Otwórz (w polu nazwy jest <see cref="PickHint"/>) albo klikając zdjęcie.
/// </summary>
public static class FolderPicker
{
    public const string PickHint = "Wybierz ten folder";
    private const uint SubfoldersCheckId = 1;
    private const int ErrorCancelled = unchecked((int)0x800704C7);

    public sealed record Result(string Folder, string? File, bool IncludeSubfolders);

    /// <param name="subfolders">null = bez pola wyboru podfolderów; inaczej jego stan początkowy.</param>
    public static Result? Show(IntPtr owner, string title, string? initialFolder, bool? subfolders)
    {
        var dialog = (IFileOpenDialog)new FileOpenDialogRcw();
        try
        {
            dialog.SetTitle(title);
            dialog.SetOkButtonLabel("Otwórz");
            dialog.SetFileTypes(2, new[]
            {
                new FilterSpec { Name = "Zdjęcia i filmy (JPG, TIFF, MP4, MOV)", Spec = "*.jpg;*.jpeg;*.tif;*.tiff;*.mp4;*.mov;*.m4v;*.3gp" },
                new FilterSpec { Name = "Wszystkie pliki", Spec = "*.*" },
            });
            dialog.GetOptions(out var options);
            options &= ~Fos.FileMustExist;
            options |= Fos.NoValidate | Fos.PathMustExist | Fos.ForceFileSystem | Fos.NoChangeDir;
            dialog.SetOptions(options);
            dialog.SetFileName(PickHint);

            if (initialFolder is not null && Directory.Exists(initialFolder) &&
                SHCreateItemFromParsingName(initialFolder, IntPtr.Zero, typeof(IShellItem).GUID, out var folderItem) == 0)
                dialog.SetFolder(folderItem);

            var customize = subfolders is not null ? (IFileDialogCustomize)dialog : null;
            if (customize is not null)
            {
                customize.AddCheckButton(SubfoldersCheckId, "Wczytaj także zdjęcia z podfolderów", subfolders == true);
                customize.MakeProminent(SubfoldersCheckId);
            }

            // po każdym przejściu do innego folderu pole nazwy wraca do „Wybierz ten folder” —
            // inaczej wpisana/wklejona ścieżka folderu zostaje w polu i „Otwórz” tylko ponownie do niego wchodzi
            var events = new ResetFileNameOnFolderChange(dialog);
            dialog.Advise(events, out var cookie);

            int hr;
            try
            {
                hr = dialog.Show(owner);
            }
            finally
            {
                dialog.Unadvise(cookie);
            }
            if (hr == ErrorCancelled) return null;
            Marshal.ThrowExceptionForHR(hr);

            dialog.GetResult(out var item);
            item.GetDisplayName(Sigdn.FileSysPath, out var path);
            var include = false;
            if (customize is not null)
                customize.GetCheckButtonState(SubfoldersCheckId, out include);

            if (Directory.Exists(path)) return new Result(path, null, include);
            if (File.Exists(path)) return new Result(Path.GetDirectoryName(path)!, path, include);
            var dir = Path.GetDirectoryName(path);
            return dir is not null && Directory.Exists(dir) ? new Result(dir, null, include) : null;
        }
        finally
        {
            Marshal.FinalReleaseComObject(dialog);
        }
    }

    // ---------------------------------------------------------------- COM

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItem item);

    [Flags]
    private enum Fos : uint
    {
        NoChangeDir = 0x8,
        ForceFileSystem = 0x40,
        NoValidate = 0x100,
        PathMustExist = 0x800,
        FileMustExist = 0x1000,
    }

    private enum Sigdn : uint
    {
        FileSysPath = 0x80058000,
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FilterSpec
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string Name;
        [MarshalAs(UnmanagedType.LPWStr)] public string Spec;
    }

    [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
    private class FileOpenDialogRcw
    {
    }

    [ComImport, Guid("d57c7288-d4ad-4768-be02-9d969532d960"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        // IModalWindow
        [PreserveSig] int Show(IntPtr parent);
        // IFileDialog
        void SetFileTypes(uint count, [MarshalAs(UnmanagedType.LPArray)] FilterSpec[] filters);
        void SetFileTypeIndex(uint index);
        void GetFileTypeIndex(out uint index);
        void Advise(IFileDialogEvents events, out uint cookie);
        void Unadvise(uint cookie);
        void SetOptions(Fos options);
        void GetOptions(out Fos options);
        void SetDefaultFolder(IShellItem item);
        void SetFolder(IShellItem item);
        void GetFolder(out IShellItem item);
        void GetCurrentSelection(out IShellItem item);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void GetResult(out IShellItem item);
        void AddPlace(IShellItem item, int place);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
        void Close(int hr);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr filter);
        // IFileOpenDialog
        void GetResults(out IntPtr items);
        void GetSelectedItems(out IntPtr items);
    }

    private sealed class ResetFileNameOnFolderChange(IFileOpenDialog dialog) : IFileDialogEvents
    {
        private const int Ok = 0;
        private const int NotImplemented = unchecked((int)0x80004001);

        public int OnFileOk(IntPtr fd) => Ok;
        public int OnFolderChanging(IntPtr fd, IntPtr folder) => Ok;

        public int OnFolderChange(IntPtr fd)
        {
            try { dialog.SetFileName(PickHint); } catch (COMException) { }
            return Ok;
        }

        public int OnSelectionChange(IntPtr fd) => Ok;
        public int OnShareViolation(IntPtr fd, IntPtr item, out int response) { response = 0; return NotImplemented; }
        public int OnTypeChange(IntPtr fd) => Ok;
        public int OnOverwrite(IntPtr fd, IntPtr item, out int response) { response = 0; return NotImplemented; }
    }

    [ComImport, Guid("973510db-7d7f-452b-8975-74a85828d354"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialogEvents
    {
        [PreserveSig] int OnFileOk(IntPtr fd);
        [PreserveSig] int OnFolderChanging(IntPtr fd, IntPtr folder);
        [PreserveSig] int OnFolderChange(IntPtr fd);
        [PreserveSig] int OnSelectionChange(IntPtr fd);
        [PreserveSig] int OnShareViolation(IntPtr fd, IntPtr item, out int response);
        [PreserveSig] int OnTypeChange(IntPtr fd);
        [PreserveSig] int OnOverwrite(IntPtr fd, IntPtr item, out int response);
    }

    [ComImport, Guid("e6fdd21a-163f-4975-9c8c-a69f1ba37034"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialogCustomize
    {
        void EnableOpenDropDown(uint id);
        void AddMenu(uint id, [MarshalAs(UnmanagedType.LPWStr)] string label);
        void AddPushButton(uint id, [MarshalAs(UnmanagedType.LPWStr)] string label);
        void AddComboBox(uint id);
        void AddRadioButtonList(uint id);
        void AddCheckButton(uint id, [MarshalAs(UnmanagedType.LPWStr)] string label, [MarshalAs(UnmanagedType.Bool)] bool isChecked);
        void AddEditBox(uint id, [MarshalAs(UnmanagedType.LPWStr)] string text);
        void AddSeparator(uint id);
        void AddText(uint id, [MarshalAs(UnmanagedType.LPWStr)] string text);
        void SetControlLabel(uint id, [MarshalAs(UnmanagedType.LPWStr)] string label);
        void GetControlState(uint id, out uint state);
        void SetControlState(uint id, uint state);
        void GetEditBoxText(uint id, out IntPtr text);
        void SetEditBoxText(uint id, [MarshalAs(UnmanagedType.LPWStr)] string text);
        void GetCheckButtonState(uint id, [MarshalAs(UnmanagedType.Bool)] out bool isChecked);
        void SetCheckButtonState(uint id, [MarshalAs(UnmanagedType.Bool)] bool isChecked);
        void AddControlItem(uint id, uint item, [MarshalAs(UnmanagedType.LPWStr)] string label);
        void RemoveControlItem(uint id, uint item);
        void RemoveAllControlItems(uint id);
        void GetControlItemState(uint id, uint item, out uint state);
        void SetControlItemState(uint id, uint item, uint state);
        void GetSelectedControlItem(uint id, out uint item);
        void SetSelectedControlItem(uint id, uint item);
        void StartVisualGroup(uint id, [MarshalAs(UnmanagedType.LPWStr)] string label);
        void EndVisualGroup();
        void MakeProminent(uint id);
        void SetControlItemText(uint id, uint item, [MarshalAs(UnmanagedType.LPWStr)] string text);
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem parent);
        void GetDisplayName(Sigdn type, [MarshalAs(UnmanagedType.LPWStr)] out string name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem other, uint hint, out int order);
    }
}
