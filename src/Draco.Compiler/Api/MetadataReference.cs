using System;
using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Draco.Compiler.Api;

/// <summary>
/// Represents some kind of metadata reference.
/// </summary>
public abstract class MetadataReference
{
    /// <summary>
    /// Retrieves the metadata reader for this reference.
    /// </summary>
    public abstract MetadataReader MetadataReader { get; }

    /// <summary>
    /// Creates a metadata reference from the given assembly.
    /// </summary>
    /// <param name="assembly">The assembly to create a metadata reader from.</param>
    /// <returns>The <see cref="MetadataReference"/> created from <paramref name="assembly"/>.</returns>
    public static MetadataReference FromAssembly(Assembly assembly)
    {
        unsafe
        {
            if (!assembly.TryGetRawMetadata(out var blob, out var length))
            {
                throw new ArgumentException("could not retrieve metadata section from assembly", nameof(assembly));
            }

            var reader = new MetadataReader(blob, length);
            return new MetadataReaderReference(reader);
        }
    }

    /// <summary>
    /// Creates a metadata reference from the given PE stream.
    /// </summary>
    /// <param name="peStream">The PE stream to create the metadata reference from.</param>
    /// <returns>The <see cref="MetadataReference"/> reading up from <paramref name="peStream"/>.</returns>
    public static MetadataReference FromPeStream(Stream peStream)
    {
        var peReader = new PEReader(peStream);
        var metadataReader = peReader.GetMetadataReader();
        return new MetadataReaderReference(metadataReader);
    }

    private sealed class MetadataReaderReference : MetadataReference
    {
        public override MetadataReader MetadataReader { get; }

        public MetadataReaderReference(MetadataReader metadataReader)
        {
            this.MetadataReader = metadataReader;
        }
    }
}
