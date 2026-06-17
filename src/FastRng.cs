using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace FastRng.ThreadSafe;

/// <summary>
/// A thread-safe pseudo-random number generator utilizing a multi-layer cascade matrix.
/// Derived from <see cref="Random"/> to seamlessly replace standard generation methods.
/// </summary>
public class FastRng : Random
{
    // Thread-local instance pattern guarantees thread safety without using locks
    [ThreadStatic] private static FastRng? _localInstance;

    // Flattened 1D array representing 6 layers of 256-byte substitution matrices
    private readonly byte[] _flatMatrix;

    private int _i;
    private int _j;

    private uint _generatedBytesCount;
    private const uint ReSeedInterval = 65536; // Re-seed threshold after generating 64KB

    /// <summary>
    /// Initializes internal state matrices, shuffles each layer, and warms up the generator.
    /// </summary>
    private FastRng()
    {
        _flatMatrix = new byte[6 * 256];

        // Fill layers with sequential values from 0 to 255
        for (int m = 0; m < 6; m++)
        {
            int offset = m << 8;
            for (int k = 0; k < 256; k++) _flatMatrix[offset + k] = (byte)k;
        }

        // Randomize the initial state pointers using cryptographic entropy
        _i = RandomNumberGenerator.GetInt32(256);
        _j = RandomNumberGenerator.GetInt32(256);

        // Randomize each layer individually
        for (int m = 0; m < 6; m++) ShuffleLayer(m);

        // Execute a warm-up phase to eliminate any initial predictable patterns
        for (int w = 0; w < 1000; w++) NextByte();
    }

    /// <summary>
    /// Provides the singleton instance isolated to the current execution thread.
    /// </summary>
    public static FastRng Instance => _localInstance ??= new FastRng();

    /// <summary>
    /// Shuffles a designated 256-byte matrix layer using the Fisher-Yates algorithm.
    /// </summary>
    private void ShuffleLayer(int layerIndex)
    {
        int offset = layerIndex << 8;
        for (int k = 255; k > 0; k--)
        {
            int idx = RandomNumberGenerator.GetInt32(k + 1);
            (_flatMatrix[offset + k], _flatMatrix[offset + idx]) = (_flatMatrix[offset + idx], _flatMatrix[offset + k]);
        }
    }

    /// <summary>
    /// Generates a single pseudo-random byte using multi-layered cascade mutations.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte NextByte()
    {
        _generatedBytesCount++;
        if (_generatedBytesCount >= ReSeedInterval)
        {
            InjectHardwareEntropy();
        }

        // Increment pointer index for the primary layer
        _i = (_i + 1) & 255;
        Span<byte> matrixSpan = _flatMatrix;
        ref byte matrixRef = ref MemoryMarshal.GetReference(matrixSpan);

        int currentOffset = 0;
        int dynamicStep = Unsafe.Add(ref matrixRef, currentOffset + _i);
        _j = (_j + dynamicStep) & 255;
        int entryIndex = (_i + dynamicStep) & 255;

        // Perform standard byte swap on the base layer
        (Unsafe.Add(ref matrixRef, currentOffset + entryIndex), Unsafe.Add(ref matrixRef, currentOffset + _j)) =
        (Unsafe.Add(ref matrixRef, currentOffset + _j), Unsafe.Add(ref matrixRef, currentOffset + entryIndex));

        // Extract intermediate state value
        int nextIndex = (Unsafe.Add(ref matrixRef, currentOffset + entryIndex) + Unsafe.Add(ref matrixRef, currentOffset + _j)) & 255;
        uint value = Unsafe.Add(ref matrixRef, currentOffset + nextIndex);

        // Calculate dynamic cascade deepness (ranging from 3 to 6 steps)
        uint targetLevels = (value % 4) + 3;
        int currentIndexForLevel = (int)value;

        // Propagate state modifications down through the underlying matrix layers
        for (uint step = 1; step < targetLevels; step++)
        {
            int currentArrayIdx = (int)((step + (value % 5)) % 6);
            int levelOffset = currentArrayIdx << 8;

            int localI = currentIndexForLevel;
            int localJ = (localI + _i + dynamicStep) & 255;

            // Execute conditional swap in the target cascade layer
            (Unsafe.Add(ref matrixRef, levelOffset + localI), Unsafe.Add(ref matrixRef, levelOffset + localJ)) =
            (Unsafe.Add(ref matrixRef, levelOffset + localJ), Unsafe.Add(ref matrixRef, levelOffset + localI));

            int finalIndex = (Unsafe.Add(ref matrixRef, levelOffset + localI) + Unsafe.Add(ref matrixRef, levelOffset + localJ)) & 255;
            value = Unsafe.Add(ref matrixRef, levelOffset + finalIndex);

            currentIndexForLevel = (int)value;
        }

        return (byte)value;
    }

    /// <summary>
    /// Highly optimized array/span generation strategy.
    /// Inlines internal generation loop directly to avoid the overhead of individual NextByte calls.
    /// </summary>
    public override void NextBytes(Span<byte> buffer)
    {
        if (buffer.IsEmpty) return;

        // Accumulate entire buffer length at once to avoid updating counter sequentially
        _generatedBytesCount += (uint)buffer.Length;
        if (_generatedBytesCount >= ReSeedInterval)
        {
            InjectHardwareEntropy();
        }

        Span<byte> matrixSpan = _flatMatrix;
        ref byte matrixRef = ref MemoryMarshal.GetReference(matrixSpan);

        // Directly fill the memory buffer without checking interval state boundaries per byte
        for (int b = 0; b < buffer.Length; b++)
        {
            _i = (_i + 1) & 255;

            int currentOffset = 0;
            int dynamicStep = Unsafe.Add(ref matrixRef, currentOffset + _i);
            _j = (_j + dynamicStep) & 255;
            int entryIndex = (_i + dynamicStep) & 255;

            (Unsafe.Add(ref matrixRef, currentOffset + entryIndex), Unsafe.Add(ref matrixRef, currentOffset + _j)) =
            (Unsafe.Add(ref matrixRef, currentOffset + _j), Unsafe.Add(ref matrixRef, currentOffset + entryIndex));

            int nextIndex = (Unsafe.Add(ref matrixRef, currentOffset + entryIndex) + Unsafe.Add(ref matrixRef, currentOffset + _j)) & 255;
            uint value = Unsafe.Add(ref matrixRef, currentOffset + nextIndex);

            uint targetLevels = (value % 4) + 3;
            int currentIndexForLevel = (int)value;

            for (uint step = 1; step < targetLevels; step++)
            {
                int currentArrayIdx = (int)((step + (value % 5)) % 6);
                int levelOffset = currentArrayIdx << 8;

                int localI = currentIndexForLevel;
                int localJ = (localI + _i + dynamicStep) & 255;

                (Unsafe.Add(ref matrixRef, levelOffset + localI), Unsafe.Add(ref matrixRef, levelOffset + localJ)) =
                (Unsafe.Add(ref matrixRef, levelOffset + localJ), Unsafe.Add(ref matrixRef, levelOffset + localI));

                int finalIndex = (Unsafe.Add(ref matrixRef, levelOffset + localI) + Unsafe.Add(ref matrixRef, levelOffset + localJ)) & 255;
                value = Unsafe.Add(ref matrixRef, levelOffset + finalIndex);

                currentIndexForLevel = (int)value;
            }

            buffer[b] = (byte)value;
        }
    }

    /// <summary>
    /// Fills the elements of a specified array of bytes with random numbers.
    /// </summary>
    public override void NextBytes(byte[] buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        NextBytes(buffer.AsSpan());
    }

    /// <summary>
    /// Generates a non-negative random integer less than <see cref="int.MaxValue"/>.
    /// </summary>
    public override int Next()
    {
        // Extract 31 bits to ensure the value is always non-negative
        return (int)(this.NextUInt64() & 0x7FFFFFFF);
    }

    /// <summary>
    /// Generates a non-negative random integer less than the specified maximum.
    /// </summary>
    public override int Next(int maxValue)
    {
        if (maxValue < 0) throw new ArgumentOutOfRangeException(nameof(maxValue), "Value must be non-negative.");
        if (maxValue == 0) return 0;

        return (int)(this.NextDouble() * maxValue);
    }

    /// <summary>
    /// Generates a random integer within the specified inclusive-exclusive range.
    /// </summary>
    public override int Next(int minValue, int maxValue)
    {
        if (minValue > maxValue) throw new ArgumentOutOfRangeException(nameof(minValue), "MinValue must be less than or equal to maxValue.");
        if (minValue == maxValue) return minValue;

        long range = (long)maxValue - minValue;
        return (int)(minValue + (this.NextDouble() * range));
    }

    /// <summary>
    /// Generates a random floating-point number greater than or equal to 0.0, and less than 1.0.
    /// </summary>
    public override double NextDouble()
    {
        // Equivalent to standard 53-bit resolution mapping for IEEE 754 doubles
        return (NextUInt64() & 0x001FFFFFFFFFFFFFUL) * (1.0 / 9007199254740992.0);
    }

    /// <summary>
    /// Private utility method to compose a 64-bit unsigned integer using the internal span loop.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ulong NextUInt64()
    {
        Unsafe.SkipInit(out ulong value);
        Span<byte> buffer = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref value, 1));
        this.NextBytes(buffer);
        return value;
    }

    /// <summary>
    /// Inject fresh hardware entropy harvested from the OS layer into internal matrix states.
    /// Defends state alignment against prediction and state reconstruction analysis.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void InjectHardwareEntropy()
    {
        _generatedBytesCount = 0;

        // Retrieve strong cryptographic chaos directly from the operating system
        Span<byte> hardwareChaos = stackalloc byte[8];
        RandomNumberGenerator.Fill(hardwareChaos);

        Span<byte> matrixSpan = _flatMatrix;
        ref byte matrixRef = ref MemoryMarshal.GetReference(matrixSpan);

        // Mix hardware entropy across all matrix layers via strategic target cell swapping
        for (int m = 0; m < 6; m++)
        {
            int levelOffset = m << 8;
            int targetCellA = hardwareChaos[m] & 255;
            int targetCellB = hardwareChaos[(m + 1) % 8] & 255;

            (Unsafe.Add(ref matrixRef, levelOffset + targetCellA), Unsafe.Add(ref matrixRef, levelOffset + targetCellB)) =
            (Unsafe.Add(ref matrixRef, levelOffset + targetCellB), Unsafe.Add(ref matrixRef, levelOffset + targetCellA));
        }

        // Apply dedicated chaos indexes [6] and [7] to securely perturb pointer positions
        _i = (_i ^ hardwareChaos[6]) & 255;
        _j = (_j ^ hardwareChaos[7]) & 255;
    }
}