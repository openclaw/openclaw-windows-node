using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;

// Only edits task-owned copies of the pinned PE fixture. The PE certificate
// table and its data-directory entry are excluded from the Authenticode digest.
internal static class TimestampFixture
{
    public static byte[] Remove(byte[] original)
    {
        var optional = checked(BitConverter.ToInt32(original, 0x3c) + 24);
        var directory = optional + (BitConverter.ToUInt16(original, optional) switch
        {
            0x10b => 96,
            0x20b => 112,
            _ => throw new InvalidDataException("Fixture is not a PE image.")
        }) + 4 * 8;
        var offset = BitConverter.ToInt32(original, directory);
        var size = BitConverter.ToInt32(original, directory + 4);
        var length = BitConverter.ToInt32(original, offset);
        if (offset <= 0 || offset + size != original.Length || ((length + 7) & ~7) != size ||
            BitConverter.ToUInt16(original, offset + 6) != 2)
            throw new InvalidDataException("Expected one final PKCS#7 certificate table.");
        var cms = new SignedCms();
        cms.Decode(original.AsSpan(offset + 8, length - 8).ToArray());
        var removed = false;
        foreach (SignerInfo signer in cms.SignerInfos)
        {
            foreach (var attribute in signer.UnsignedAttributes.Cast<CryptographicAttributeObject>().ToArray())
            {
                foreach (AsnEncodedData value in attribute.Values)
                    signer.RemoveUnsignedAttribute(value);
                removed = true;
            }
        }
        if (!removed) throw new InvalidDataException("Pinned fixture had no removable timestamp attributes.");
        var encoded = cms.Encode();
        var newLength = encoded.Length + 8;
        var padded = (newLength + 7) & ~7;
        var result = new byte[offset + padded];
        original.AsSpan(0, offset + 8).CopyTo(result);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(directory + 4), padded);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(offset), newLength);
        encoded.CopyTo(result, offset + 8);
        return result;
    }
}
