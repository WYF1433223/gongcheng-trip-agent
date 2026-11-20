using System;
using System.IO;
using UnityEngine;

/// <summary>
/// 系统分享桥：Android 走 Intent 分享面板（可转发微信 / QQ / 短信 / 邮件），
/// 桌面与编辑器里退化为复制到剪贴板，保证同一套按钮在所有平台都能用。
/// </summary>
public static class Share
{
    const string ChooserTitle = "分享行程";

    /// <summary>把纯文本交给系统分享面板。返回 false 表示当前平台不支持，调用方应回退到剪贴板。</summary>
    public static bool SendText(string subject, string text)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            using (var intentClass = new AndroidJavaClass("android.content.Intent"))
            using (var intent = new AndroidJavaObject("android.content.Intent"))
            {
                intent.Call<AndroidJavaObject>("setAction", intentClass.GetStatic<string>("ACTION_SEND"));
                intent.Call<AndroidJavaObject>("setType", "text/plain");
                intent.Call<AndroidJavaObject>("putExtra", intentClass.GetStatic<string>("EXTRA_SUBJECT"), subject);
                intent.Call<AndroidJavaObject>("putExtra", intentClass.GetStatic<string>("EXTRA_TEXT"), text);
                using (var chooser = intentClass.CallStatic<AndroidJavaObject>("createChooser", intent, ChooserTitle))
                using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                {
                    activity.Call("startActivity", chooser);
                }
            }
            return true;
        }
        catch (Exception e)
        {
            Debug.Log("[Share] text failed: " + e.Message);
            return false;
        }
#else
        return false;
#endif
    }

    /// <summary>把一张 PNG 写进系统相册（Android 用 MediaStore，桌面用 Pictures 目录）。</summary>
    public static bool SaveImageToGallery(string path, string fileName)
    {
        if (!File.Exists(path)) return false;
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
            using (var resolver = activity.Call<AndroidJavaObject>("getContentResolver"))
            using (var values = new AndroidJavaObject("android.content.ContentValues"))
            using (var media = new AndroidJavaClass("android.provider.MediaStore$Images$Media"))
            {
                values.Call("put", "_display_name", fileName);
                values.Call("put", "mime_type", "image/png");
                using (var uri = resolver.Call<AndroidJavaObject>("insert",
                    media.GetStatic<AndroidJavaObject>("EXTERNAL_CONTENT_URI"), values))
                {
                    if (uri == null) return false;
                    using (var os = resolver.Call<AndroidJavaObject>("openOutputStream", uri))
                    {
                        if (os == null) return false;
                        os.Call("write", File.ReadAllBytes(path));
                        os.Call("flush");
                        os.Call("close");
                    }
                }
            }
            return true;
        }
        catch (Exception e)
        {
            Debug.Log("[Share] gallery failed: " + e.Message);
            AskStoragePermission();
            return false;
        }
#else
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "拱程");
            Directory.CreateDirectory(dir);
            File.Copy(path, Path.Combine(dir, fileName), true);
            return true;
        }
        catch (Exception e)
        {
            Debug.Log("[Share] gallery copy failed: " + e.Message);
            return false;
        }
#endif
    }

    /// <summary>Android 7–9 写相册需要存储权限，失败后弹一次系统授权框，用户同意后再点保存即可。</summary>
    static void AskStoragePermission()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            using (var version = new AndroidJavaClass("android.os.Build$VERSION"))
            {
                int sdk = version.GetStatic<int>("SDK_INT");
                if (sdk >= 29) return;   // Android 10 起自己写入的媒体不需要权限
            }
            using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
            {
                activity.Call("requestPermissions",
                    new[] { "android.permission.WRITE_EXTERNAL_STORAGE" }, 771);
            }
            Debug.Log("[Share] requested WRITE_EXTERNAL_STORAGE for legacy gallery write");
        }
        catch (Exception e)
        {
            Debug.Log("[Share] permission request failed: " + e.Message);
        }
#endif
    }

    /// <summary>把相册里刚存好的那张图再交给分享面板（微信可直接发图片）。</summary>
    public static bool SendImage(string fileName, string text)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
            using (var resolver = activity.Call<AndroidJavaObject>("getContentResolver"))
            using (var media = new AndroidJavaClass("android.provider.MediaStore$Images$Media"))
            {
                string selection = "_display_name = ?";
                var args = new[] { fileName };
                using (var cursor = resolver.Call<AndroidJavaObject>("query",
                    media.GetStatic<AndroidJavaObject>("EXTERNAL_CONTENT_URI"),
                    new[] { "_id" }, selection, args, null))
                {
                    if (cursor == null || !cursor.Call<bool>("moveToFirst")) return false;
                    long id = cursor.Call<long>("getLong", cursor.Call<int>("getColumnIndexOrThrow", "_id"));
                    cursor.Call("close");
                    using (var contentUris = new AndroidJavaClass("android.content.ContentUris"))
                    using (var uri = contentUris.CallStatic<AndroidJavaObject>("withAppendedId",
                        media.GetStatic<AndroidJavaObject>("EXTERNAL_CONTENT_URI"), id))
                    using (var intentClass = new AndroidJavaClass("android.content.Intent"))
                    using (var intent = new AndroidJavaObject("android.content.Intent"))
                    {
                        intent.Call<AndroidJavaObject>("setAction", intentClass.GetStatic<string>("ACTION_SEND"));
                        intent.Call<AndroidJavaObject>("setType", "image/png");
                        intent.Call<AndroidJavaObject>("putExtra", intentClass.GetStatic<string>("EXTRA_STREAM"), uri);
                        intent.Call<AndroidJavaObject>("putExtra", intentClass.GetStatic<string>("EXTRA_TEXT"), text);
                        intent.Call<AndroidJavaObject>("addFlags", intentClass.GetStatic<int>("FLAG_GRANT_READ_URI_PERMISSION"));
                        using (var chooser = intentClass.CallStatic<AndroidJavaObject>("createChooser", intent, ChooserTitle))
                        {
                            activity.Call("startActivity", chooser);
                        }
                    }
                }
            }
            return true;
        }
        catch (Exception e)
        {
            Debug.Log("[Share] image failed: " + e.Message);
            return false;
        }
#else
        return false;
#endif
    }
}

