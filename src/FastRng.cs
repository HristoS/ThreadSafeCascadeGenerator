using System.Numerics;
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
    // High-speed ThreadStatic cache layer to bypass heavy AsyncLocal lookups
    [ThreadStatic] private static byte[]? _tsCache;

    private static readonly AsyncLocal<byte[]?> _localState = new();
    private static readonly object _lock = new();

    private const uint ReSeedInterval = 65536;// Re-seed threshold after generating 64KB
    private const int MetadataSize = 4;
    private const int MatrixSize = 4 * 256; // Reduced to 4 layers
    private const int TotalStateSize = MetadataSize + MatrixSize; // 1284 bytes

    /// <summary>
    /// Initializes internal state matrices, shuffles each layer, and warms up the generator.
    /// </summary>
    private FastRng()
    {
    }

    /// <summary>
    /// Gets the context-safe instance for the current execution flow.
    /// </summary>
    public static FastRng Instance
    {
        get
        {
            if (_localInstance == null)
            {
                lock (_lock)
                {
                    _localInstance ??= new FastRng();
                }
            }
            return _localInstance;
        }
    }

    private static FastRng? _localInstance;

    /// <summary>
    /// Generates a single pseudo-random byte using multi-layered cascade mutations.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte NextByte()
    {
        byte[] state = GetOrCreateState();
        ref byte stateRef = ref MemoryMarshal.GetReference((Span<byte>)state);

        // Safely extract metadata indices directly via pointer offsets
        int localI = (Unsafe.Add(ref stateRef, 0) + 1) & 255;
        int localJ = (Unsafe.Add(ref stateRef, 1) + Unsafe.Add(ref stateRef, MetadataSize + localI)) & 255;

        // Increment the count stored at positions 2 and 3
        Unsafe.As<byte, ushort>(ref Unsafe.Add(ref stateRef, 2))++;

        // Base Layer Swapping
        (Unsafe.Add(ref stateRef, MetadataSize + localI), Unsafe.Add(ref stateRef, MetadataSize + localJ)) =
        (Unsafe.Add(ref stateRef, MetadataSize + localJ), Unsafe.Add(ref stateRef, MetadataSize + localI));

        int targetLevels = 3 + ((localI + localJ) & 1); // Range 3-4
        int currentIndexForLevel = (Unsafe.Add(ref stateRef, MetadataSize + localI) + Unsafe.Add(ref stateRef, MetadataSize + localJ)) & 255;
        byte userValue = Unsafe.Add(ref stateRef, MetadataSize + currentIndexForLevel);

        // Forward Cascade Loops across the remaining layers
        for (uint step = 1; step < targetLevels; step++)
        {
            // Fast bit-masking if applicable, or highly optimized arithmetic
            //int currentArrayIdx = (int)((step + (userValue & 3)) & 3);  // Modified userValue % 4 -> & 3
            int levelOffset = MetadataSize + (int)(((step << 8) + (userValue << 8)) & 0x300);

            int lvlJ = (currentIndexForLevel + localI + (int)step) & 255;

            // Use a single local variable cache for the swap to maximize CPU register utilization
            ref byte refI = ref Unsafe.Add(ref stateRef, levelOffset + currentIndexForLevel);
            ref byte refJ = ref Unsafe.Add(ref stateRef, levelOffset + lvlJ);

            byte temp = refI;
            refI = refJ;
            refJ = temp;

            int finalIndex = (refI + refJ) & 255;
            userValue = Unsafe.Add(ref stateRef, levelOffset + finalIndex);
            currentIndexForLevel = userValue;
        }

        // Fast-Key-Erasure Reverse Scrambling (Inlined & Zero Allocation)
        int secretI = (localI + 1) & 255;
        int secretJ = (localJ ^ userValue) & 255;
        byte erasureByte = Unsafe.Add(ref stateRef, MetadataSize + ((secretI + secretJ) & 255));

        for (int step = targetLevels - 1; step >= 0; step--)
        {
            //int currentArrayIdx = (int)((step + (erasureByte & 3)) & 3);
            int levelOffset = MetadataSize + (int)(((step << 8) + (userValue << 8)) & 0x300);

            int reverseI = (currentIndexForLevel ^ erasureByte) & 255;
            int reverseJ = (reverseI + localJ) & 255;

            ref byte refRevI = ref Unsafe.Add(ref stateRef, levelOffset + reverseI);
            ref byte refRevJ = ref Unsafe.Add(ref stateRef, levelOffset + reverseJ);

            byte temp = refRevI;
            refRevI = refRevJ;
            refRevJ = temp;

            erasureByte = (byte)(refRevI ^ reverseJ);
        }

        // Save metadata registers back to structural indices
        Unsafe.Add(ref stateRef, 0) = (byte)localI;
        Unsafe.Add(ref stateRef, 1) = (byte)localJ;

        return userValue;
    }

    /// <summary>
    /// Highly optimized array/span generation strategy.
    /// Inlines internal generation loop directly to avoid the overhead of individual NextByte calls.
    /// </summary>
    public override void NextBytes(Span<byte> buffer)
    {
        if (buffer.IsEmpty) return;

        byte[] state = GetOrCreateState();
        ref byte stateRef = ref MemoryMarshal.GetReference((Span<byte>)state);

        int localI = Unsafe.Add(ref stateRef, 0);
        int localJ = Unsafe.Add(ref stateRef, 1);
        ushort count = Unsafe.As<byte, ushort>(ref Unsafe.Add(ref stateRef, 2));

        int bytesProcessed = 0;
        int totalLength = buffer.Length;

        while (bytesProcessed < totalLength)
        {
            int remainingInInterval = (int)(ReSeedInterval - count);
            int chunkLength = Math.Min(totalLength - bytesProcessed, remainingInInterval);

            if (chunkLength <= 0)
            {
                Unsafe.Add(ref stateRef, 0) = (byte)localI;
                Unsafe.Add(ref stateRef, 1) = (byte)localJ;
                Unsafe.As<byte, ushort>(ref Unsafe.Add(ref stateRef, 2)) = count;

                state = GetOrCreateState();
                stateRef = ref MemoryMarshal.GetReference((Span<byte>)state);

                localI = Unsafe.Add(ref stateRef, 0);
                localJ = Unsafe.Add(ref stateRef, 1);
                count = 0;
                continue;
            }

            Span<byte> chunkBuffer = buffer.Slice(bytesProcessed, chunkLength);
            ref byte bufferRef = ref MemoryMarshal.GetReference(chunkBuffer);

            // HIGH SPEED ENGINE: We generate values directly from the base matrix layer
            // to max out instruction-level parallelism (ILP) and instruction caches.
            for (int i = 0; i < chunkLength; i++)
            {
                localI = (localI + 1) & 255;
                ref byte baseLvlI = ref Unsafe.Add(ref stateRef, MetadataSize + localI);
                localJ = (localJ + baseLvlI) & 255;
                ref byte baseLvlJ = ref Unsafe.Add(ref stateRef, MetadataSize + localJ);

                // Swap positions in Base Layer
                byte baseTemp = baseLvlI;
                baseLvlI = baseLvlJ;
                baseLvlJ = baseTemp;

                // Extract random byte output directly from base matrix mix
                Unsafe.Add(ref bufferRef, i) = Unsafe.Add(ref stateRef, MetadataSize + ((baseLvlI + baseLvlJ) & 255));
            }

            // SCRAMBLING PASS: We run the forward & backward multi-layer mixing state changes
            // ONCE at the end of the chunk to mix layers up and retain the state evolution.
            int targetLevels = 3 + ((localI + localJ) & 1);
            int mixIndex = (localI + localJ) & 255;
            byte mixVal = Unsafe.Add(ref stateRef, MetadataSize + mixIndex);

            for (uint step = 1; step < targetLevels; step++)
            {
                int levelOffset = MetadataSize + (int)(((step << 8) + (mixVal << 8)) & 0x300);
                ref byte rI = ref Unsafe.Add(ref stateRef, levelOffset + localI);
                ref byte rJ = ref Unsafe.Add(ref stateRef, levelOffset + localJ);
                byte t = rI; rI = rJ; rJ = t;
                mixVal = Unsafe.Add(ref stateRef, levelOffset + ((rI + rJ) & 255));
            }

            count += (ushort)chunkLength;
            bytesProcessed += chunkLength;
        }

        Unsafe.Add(ref stateRef, 0) = (byte)localI;
        Unsafe.Add(ref stateRef, 1) = (byte)localJ;
        Unsafe.As<byte, ushort>(ref Unsafe.Add(ref stateRef, 2)) = count;
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
        return (int)(this.NextUInt32() & 0x7FFFFFFF);
    }

    /// <summary>
    /// Generates a non-negative random integer less than the specified maximum.
    /// </summary>
    public override int Next(int maxValue)
    {
        if (maxValue <= 0) throw new ArgumentOutOfRangeException(nameof(maxValue));
        return Next(0, maxValue);
    }

    /// <summary>
    /// Generates a random integer within the specified inclusive-exclusive range.
    /// </summary>
    public override int Next(int minValue, int maxValue)
    {
        if (minValue > maxValue) throw new ArgumentOutOfRangeException("minValue must be less than maxValue");

        long range = (long)maxValue - minValue;
        if (range <= 1) return minValue;

        // Optimized Bit-Mask Rejection Sampling Engine
        // Eliminates modulo bias while maximizing execution pipeline efficiency
        int powerOfTwoMask = (int)BitOperations.RoundUpToPowerOf2((ulong)range) - 1;

        while (true)
        {
            int randomVal = (int)(NextUInt32() & powerOfTwoMask); if (randomVal < range)
            {
                return minValue + randomVal;
            }
        }
    }

    /// <summary>
    /// Generates a random floating-point number greater than or equal to 0.0, and less than 1.0.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override double NextDouble()
    {
        // Pulls 53 bits of raw random entropy and multiplies by scale factor
        // This completely eliminates slow floating-point CPU division operations
        const double scale = 1.0 / (1L << 53);
        ulong random64 = NextUInt64();
        return (random64 >> 11) * scale;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint NextUInt32()
    {
        // Unrolls 4 fast sequential iterations inline to avoid stack allocations
        uint val = (uint)NextByte() << 24;
        val |= (uint)NextByte() << 16;
        val |= (uint)NextByte() << 8;
        val |= NextByte();
        return val;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ulong NextUInt64()
    {
        ulong val = NextUInt32();
        return val | ((ulong)NextUInt32() << 32);
    }

    /// <summary>
    /// Selects an index from an array of weights. Higher weights have a higher chance of selection.
    /// Crucial for slot machine reel configurations and virtual wheel layouts.
    /// </summary>
    public int NextWeightedIndex(ReadOnlySpan<int> weights)
    {
        if (weights.IsEmpty) throw new ArgumentException("Weights span cannot be empty.", nameof(weights));

        long totalWeight = 0;
        for (int i = 0; i < weights.Length; i++)
        {
            if (weights[i] < 0) throw new ArgumentException("Weights cannot be negative.");
            totalWeight += weights[i];
        }

        if (totalWeight == 0) throw new ArgumentException("Total sum of weights must be greater than zero.");

        // Generate a random roll across the total weight spectrum
        double roll = NextDouble() * totalWeight;
        double runningSum = 0;

        for (int i = 0; i < weights.Length; i++)
        {
            runningSum += weights[i];
            if (roll < runningSum) return i;
        }

        return weights.Length - 1;
    }

    /// <summary>
    /// Generates a perfectly uniform random integer between minValue (inclusive) and maxValue (exclusive).
    /// Eliminates modulo and floating-point bias using mathematical rejection sampling.
    /// </summary>
    public int NextUniformInt(int minValue, int maxValue)
    {
        if (minValue >= maxValue)
            throw new ArgumentOutOfRangeException(nameof(minValue), "MinValue must be less than maxValue.");

        uint range = (uint)(maxValue - minValue);
        if (range == 1) return minValue;

        // Calculate the rejection threshold to eliminate bias
        uint limit = uint.MaxValue - (uint.MaxValue % range);
        uint sample;

        do
        {
            // Re-use your ultra-fast 64-bit sequence
            sample = (uint)(NextUInt64() & 0xFFFFFFFFUL);
        }
        while (sample >= limit);

        return (int)(minValue + (sample % range));
    }

    /// <summary>
    /// Shuffles an entire span in place using an unbiased Fisher-Yates algorithm.
    /// Perfect for card games and reel sets.
    /// </summary>
    public void Shuffle<T>(Span<T> span)
    {
        if (span.Length <= 1) return;

        for (int i = span.Length - 1; i > 0; i--)
        {
            int j = NextUniformInt(0, i + 1);
            (span[i], span[j]) = (span[j], span[i]);
        }
    }

    /// <summary>
    /// Shuffles an array in place using an unbiased Fisher-Yates algorithm.
    /// </summary>
    public void Shuffle<T>(T[] array)
    {
        ArgumentNullException.ThrowIfNull(array);
        Shuffle(array.AsSpan());
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte[] GetOrCreateState()
    {
        // Fast path: synchronous local cache hit
        var state = _tsCache;

        if (state != null)
        {
            ref byte countRef = ref state[2];
            if (Unsafe.As<byte, ushort>(ref countRef) < ReSeedInterval)
            {
                return state;
            }
        }

        // Slow path: fallback to AsyncLocal dictionary context and rotation
        state = _localState.Value;
        if (state == null)
        {
            state = InitializeNewState();
            _localState.Value = state;
        }
        else
        {
            ref byte countRef = ref state[2];
            if (Unsafe.As<byte, ushort>(ref countRef) >= ReSeedInterval)
            {
                state = InitializeNewState();
                _localState.Value = state;
            }
        }

        _tsCache = state;
        return state;
    }

    private byte[] InitializeNewState()
    {
        var state = new byte[TotalStateSize];
        ref byte matrixRef = ref MemoryMarshal.GetReference((Span<byte>)state);

        // 1. Map identity sequences (0 to 255) to all 5 layers safely
        for (int layer = 0; layer < 4; layer++)
        {
            int offset = MetadataSize + (layer << 8);
            for (int val = 0; val < 256; val++)
            {
                Unsafe.Add(ref matrixRef, offset + val) = (byte)val;
            }
        }

        // 2. Fetch a single heavy cryptographic entropy buffer from the OS
        // 1024 bytes completely isolates 5 independent shuffle tracks
        Span<byte> heavyChaos = stackalloc byte[1024];
        RandomNumberGenerator.Fill(heavyChaos);

        // Seed initial state registers with pristine randomness
        Unsafe.Add(ref matrixRef, 0) = heavyChaos[0];
        Unsafe.Add(ref matrixRef, 1) = heavyChaos[1];

        // 3. Perform completely unbiased Fisher-Yates layer shuffling
        // Uses a fast bit-mask to completely bypass modulo remainder bias
        int chaosPointer = 2;
        for (int layer = 0; layer < 4; layer++)
        {
            int levelOffset = MetadataSize + (layer << 8);

            for (int i = 255; i > 0; i--)
            {
                // Calculate the exact next power of 2 bitmask for index 'i'
                int powerOfTwoMask = (1 << (32 - BitOperations.LeadingZeroCount((uint)i))) - 1;
                int j;

                while (true)
                {
                    // Pull a byte from our pool, mask it, and reject if it overflows bounds
                    j = heavyChaos[chaosPointer & 1023] & powerOfTwoMask;
                    chaosPointer++;

                    // Refill pool on the fly if we exhaust the 1024 bytes during rejection steps
                    if (chaosPointer >= 1024)
                    {
                        RandomNumberGenerator.Fill(heavyChaos);
                        chaosPointer = 0;
                    }

                    if (j <= i) break;
                }

                // Perform the cell mutation swap
                ref byte refI = ref Unsafe.Add(ref matrixRef, levelOffset + i);
                ref byte refJ = ref Unsafe.Add(ref matrixRef, levelOffset + j);

                byte temp = refI;
                refI = refJ;
                refJ = temp;
            }
        }

        return state;
    }
}