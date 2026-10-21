using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Hkmp.Util;

/// <summary>
/// The on-disk format of Hollow Knight save slots (<c>userN.dat</c>) on PC. With save encryption enabled (the PC
/// default) the game stores the save JSON AES-encrypted (ECB, PKCS7, fixed key), Base64 encoded and wrapped in a
/// .NET BinaryFormatter stream holding that single string, and it only reads that format back.
/// <para>The Unity 6 port of the Modding API replaces <c>GameManager.SaveGame</c> and writes the JSON in plain
/// text, which the (vanilla) loader then rejects ("The input stream is not a valid binary format"), so the slot
/// shows up as corrupted and can no longer be continued. These helpers convert between both formats without
/// using BinaryFormatter (not available everywhere) and without game dependencies, so they are unit tested.</para>
/// </summary>
internal static class SaveFileFormat {
    /// <summary>
    /// The key the game uses for save encryption (TeamCherry.SharedUtils.Encryption).
    /// </summary>
    private static readonly byte[] Key = Encoding.UTF8.GetBytes("UKu52ePUBwetZ9wNX88o54dnfKRu0T1l");

    /// <summary>
    /// BinaryFormatter SerializationHeaderRecord (root id 1, header id -1, version 1.0) followed by the record type
    /// of a BinaryObjectString (6) with object id 1.
    /// </summary>
    private static readonly byte[] Header = [
        0x00, 0x01, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x06, 0x01, 0x00, 0x00, 0x00
    ];

    /// <summary>
    /// BinaryFormatter MessageEnd record.
    /// </summary>
    private const byte MessageEnd = 0x0B;

    /// <summary>
    /// Whether the given save bytes are plain (unencrypted) JSON, i.e. the first non-whitespace character (after an
    /// optional UTF-8 BOM) is an opening brace.
    /// </summary>
    public static bool IsPlainJson(byte[] bytes) {
        if (bytes == null) {
            return false;
        }

        var i = 0;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) {
            i = 3;
        }

        for (; i < bytes.Length; i++) {
            var b = bytes[i];
            if (b == ' ' || b == '\t' || b == '\r' || b == '\n') {
                continue;
            }

            return b == '{';
        }

        return false;
    }

    /// <summary>
    /// Encode save JSON in the encrypted format the game reads when save encryption is enabled.
    /// </summary>
    public static byte[] Encrypt(string json) {
        var plain = Encoding.UTF8.GetBytes(json);
        byte[] cipher;
        using (var aes = CreateAes()) {
            using var encryptor = aes.CreateEncryptor();
            cipher = encryptor.TransformFinalBlock(plain, 0, plain.Length);
        }

        var base64 = Encoding.UTF8.GetBytes(Convert.ToBase64String(cipher));

        using var stream = new MemoryStream(Header.Length + base64.Length + 6);
        stream.Write(Header, 0, Header.Length);
        // LengthPrefixedString: 7-bit encoded length
        var length = (uint) base64.Length;
        while (length >= 0x80) {
            stream.WriteByte((byte) (length | 0x80));
            length >>= 7;
        }

        stream.WriteByte((byte) length);
        stream.Write(base64, 0, base64.Length);
        stream.WriteByte(MessageEnd);
        return stream.ToArray();
    }

    /// <summary>
    /// Encode plain JSON save bytes in the encrypted format.
    /// </summary>
    public static byte[] EncryptPlainJson(byte[] plainJson) {
        var json = Encoding.UTF8.GetString(plainJson);
        if (json.Length > 0 && json[0] == '﻿') {
            json = json.Substring(1);
        }

        return Encrypt(json);
    }

    /// <summary>
    /// Try to decode save bytes in the encrypted format back to the save JSON.
    /// </summary>
    /// <returns>True if the bytes were in the encrypted format and could be decoded, false otherwise.</returns>
    public static bool TryDecrypt(byte[] bytes, out string json) {
        json = null;
        if (bytes == null || bytes.Length < Header.Length + 2) {
            return false;
        }

        for (var i = 0; i < Header.Length; i++) {
            if (bytes[i] != Header[i]) {
                return false;
            }
        }

        try {
            var offset = Header.Length;
            var length = 0;
            var shift = 0;
            byte current;
            do {
                if (offset >= bytes.Length || shift > 28) {
                    return false;
                }

                current = bytes[offset++];
                length |= (current & 0x7F) << shift;
                shift += 7;
            } while ((current & 0x80) != 0);

            if (length < 0 || offset + length > bytes.Length) {
                return false;
            }

            var cipher = Convert.FromBase64String(Encoding.UTF8.GetString(bytes, offset, length));
            using var aes = CreateAes();
            using var decryptor = aes.CreateDecryptor();
            json = Encoding.UTF8.GetString(decryptor.TransformFinalBlock(cipher, 0, cipher.Length));
            return true;
        } catch (Exception) {
            json = null;
            return false;
        }
    }

    private static Aes CreateAes() {
        var aes = Aes.Create();
        aes.Key = Key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.PKCS7;
        return aes;
    }
}
