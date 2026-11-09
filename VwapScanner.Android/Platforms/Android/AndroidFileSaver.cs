using Android.Content;
using Android.Provider;

namespace VwapScanner.Mobile;

/// <summary>Writes a CSV into the public Downloads\VwapScanner folder through MediaStore (Android 10+; needs no storage permission).</summary>
public sealed class AndroidFileSaver : IFileSaver
{
    private const string Folder = "Download/VwapScanner";

    public Task<string?> SaveCsvAsync(string fileName, string content)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(29))
            return Task.FromResult<string?>(null);
        return Task.Run<string?>(() =>
        {
            var resolver =
                Android.App.Application.Context.ContentResolver
                ?? throw new IOException("No content resolver.");
            var values = new ContentValues();
            values.Put(MediaStore.IMediaColumns.DisplayName, fileName);
            values.Put(MediaStore.IMediaColumns.MimeType, "text/csv");
            values.Put(MediaStore.IMediaColumns.RelativePath, Folder);
            var uri =
                resolver.Insert(MediaStore.Downloads.ExternalContentUri!, values)
                ?? throw new IOException("Android refused to create the file.");
            using var stream =
                resolver.OpenOutputStream(uri)
                ?? throw new IOException("Could not open the new file.");
            var bytes = new System.Text.UTF8Encoding(false).GetBytes(content);
            stream.Write(bytes, 0, bytes.Length);
            return $"{Folder}/{fileName}";
        });
    }
}
