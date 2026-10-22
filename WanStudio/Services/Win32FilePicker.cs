using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace WanStudio.Services;

/// <summary>
/// 纯 Win32 文件对话框（GetOpenFileNameW / GetSaveFileNameW，comdlg32）。
///
/// 为什么不用 WinRT 的 FileOpenPicker：在「未打包」WinUI 3 应用里，
/// PickMultipleFilesAsync() 会直接抛 COMException E_FAIL(0x80004005)，
/// 对话框根本不弹（HRESULT=0x80004005 就是这个）。这是 MSIX 缺失导致的限制，
/// InitializeWithWindow 也救不了。
///
/// GetOpenFileNameW 是 Win32 经典 API，不依赖包身份、不依赖 COM 激活，
/// 在 unpackaged 应用里稳定工作。样式旧一点，但多选/过滤/Unicode 都完整。
/// </summary>
public static class Win32FilePicker
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OPENFILENAMEW
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public string lpstrFilter;
        public string lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public IntPtr lpstrFile;
        public int nMaxFile;
        public string lpstrFileTitle;
        public int nMaxFileTitle;
        public string lpstrInitialDir;
        public string lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public string lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public string lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetOpenFileNameW(ref OPENFILENAMEW ofn);

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetSaveFileNameW(ref OPENFILENAMEW ofn);

    private const int OFN_EXPLORER = 0x00080000;
    private const int OFN_ALLOWMULTISELECT = 0x00000200;
    private const int OFN_FILEMUSTEXIST = 0x00001000;
    private const int OFN_PATHMUSTEXIST = 0x00000800;
    private const int OFN_OVERWRITEPROMPT = 0x00000002;
    private const int OFN_NOCHANGEDIR = 0x00000008;

    /// <summary>缓冲区大小（Unicode 字符数）。多选时是「目录\0文件名1\0文件名2\0\0」。</summary>
    private const int BufferChars = 16384;

    /// <summary>
    /// 弹「打开文件」对话框（支持多选）。
    /// 返回选中的绝对路径；取消或失败返回空列表。
    /// </summary>
    public static IReadOnlyList<string> PickFiles(
        IntPtr ownerHwnd, string title, IReadOnlyList<string> extensions)
    {
        var buffer = Marshal.AllocHGlobal(BufferChars * 2);
        try
        {
            // 必须清零：缓冲区末尾缺少 '\0' 会让解析出错。
            for (var i = 0; i < BufferChars; i++) Marshal.WriteInt16(buffer, i * 2, (short)0);

            var ofn = new OPENFILENAMEW
            {
                lStructSize = Marshal.SizeOf<OPENFILENAMEW>(),
                hwndOwner = ownerHwnd,
                lpstrFilter = BuildFilter(extensions),
                nFilterIndex = 1,
                lpstrFile = buffer,
                nMaxFile = BufferChars,
                lpstrTitle = string.IsNullOrWhiteSpace(title) ? "选择文件" : title,
                Flags = OFN_EXPLORER | OFN_ALLOWMULTISELECT | OFN_FILEMUSTEXIST
                      | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR
            };

            if (!GetOpenFileNameW(ref ofn)) return Array.Empty<string>();
            return ParseBuffer(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>弹「另存为」对话框。返回选中的完整路径；取消返回 null。</summary>
    public static string? PickSaveFile(
        IntPtr ownerHwnd, string title, string suggestedName, string defaultExtension,
        IReadOnlyList<(string Label, string Pattern)> filters)
    {
        var buffer = Marshal.AllocHGlobal(BufferChars * 2);
        try
        {
            for (var i = 0; i < BufferChars; i++) Marshal.WriteInt16(buffer, i * 2, (short)0);
            if (!string.IsNullOrWhiteSpace(suggestedName)) WriteString(buffer, suggestedName);

            var ofn = new OPENFILENAMEW
            {
                lStructSize = Marshal.SizeOf<OPENFILENAMEW>(),
                hwndOwner = ownerHwnd,
                lpstrFilter = BuildNamedFilter(filters),
                nFilterIndex = 1,
                lpstrFile = buffer,
                nMaxFile = BufferChars,
                lpstrTitle = string.IsNullOrWhiteSpace(title) ? "另存为" : title,
                lpstrDefExt = string.IsNullOrWhiteSpace(defaultExtension) ? "" : defaultExtension,
                Flags = OFN_EXPLORER | OFN_PATHMUSTEXIST | OFN_OVERWRITEPROMPT | OFN_NOCHANGEDIR
            };

            if (!GetSaveFileNameW(ref ofn)) return null;
            var picked = ParseBuffer(buffer);
            return picked.Count > 0 ? picked[0] : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // WinRT 风格的异步包装（调用点不用改写法）。
    public static System.Threading.Tasks.Task<IReadOnlyList<string>> PickFilesAsync(
        IntPtr ownerHwnd, string title, IReadOnlyList<string> extensions)
        => System.Threading.Tasks.Task.FromResult(PickFiles(ownerHwnd, title, extensions));

    public static System.Threading.Tasks.Task<string?> PickSaveFileAsync(
        IntPtr ownerHwnd, string title, string suggestedName, string defaultExtension,
        IReadOnlyList<(string Label, string Pattern)> filters)
        => System.Threading.Tasks.Task.FromResult(
            PickSaveFile(ownerHwnd, title, suggestedName, defaultExtension, filters));

    private static void WriteString(IntPtr buffer, string value)
    {
        for (var i = 0; i < value.Length && i < BufferChars - 1; i++)
            Marshal.WriteInt16(buffer, i * 2, (short)value[i]);
    }

    /// <summary>
    /// 解析缓冲区。单选 = 一个完整路径；多选 = 「目录 + 各文件名」。
    /// </summary>
    private static List<string> ParseBuffer(IntPtr buffer)
    {
        var raw = Marshal.PtrToStringUni(buffer, BufferChars) ?? "";
        var parts = new List<string>();

        var i = 0;
        while (i < raw.Length && raw[i] == '\0') i++;
        if (i >= raw.Length || raw[i] == '\0') return parts;

        var end = raw.IndexOf('\0', i);
        var first = end < 0 ? raw[i..] : raw.Substring(i, end - i);
        if (first.Length == 0) return parts;

        // 第一段就是存在的文件 → 单选。
        if (File.Exists(first)) { parts.Add(first); return parts; }

        // 否则第一段是目录，后面跟各文件名（多选）。
        var dir = first;
        i = end < 0 ? raw.Length : end + 1;
        while (i < raw.Length && raw[i] != '\0')
        {
            var e2 = raw.IndexOf('\0', i);
            var name = e2 < 0 ? raw[i..] : raw.Substring(i, e2 - i);
            if (name.Length > 0)
            {
                var combined = Path.Combine(dir, name);
                if (File.Exists(combined)) parts.Add(combined);
            }
            i = e2 < 0 ? raw.Length : e2 + 1;
        }
        return parts;
    }

    /// <summary>过滤器字符串（双 '\0' 结尾）。</summary>
    private static string BuildFilter(IReadOnlyList<string> extensions)
    {
        var pats = new List<string>();
        foreach (var e in extensions)
        {
            var t = e.Trim();
            if (t.Length == 0) continue;
            pats.Add(t.StartsWith("*.") ? t : "*." + t.TrimStart('.'));
        }
        if (pats.Count == 0) return "所有文件 (*.*)\0*.*\0";
        var pattern = string.Join(";", pats);
        return $"支持的媒体文件 ({pattern})\0{pattern}\0所有文件 (*.*)\0*.*\0";
    }

    private static string BuildNamedFilter(IReadOnlyList<(string Label, string Pattern)> filters)
    {
        if (filters.Count == 0) return "所有文件 (*.*)\0*.*\0";
        var sb = new StringBuilder();
        foreach (var f in filters)
            sb.Append(f.Label).Append('\0').Append(f.Pattern).Append('\0');
        sb.Append("所有文件 (*.*)\0*.*\0");
        return sb.ToString();
    }
}
