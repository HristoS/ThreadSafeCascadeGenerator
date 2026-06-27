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

    private const uint ReSeedInterval = 65535;// Re-seed threshold after generating 64KB
    private const int MetadataSize = 4;
    private const int MatrixSize = 16 * 256; // Reduced to 4 layers
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

        int localI = Unsafe.Add(ref stateRef, 0);
        int localJ = Unsafe.Add(ref stateRef, 1);
        ushort count = Unsafe.As<byte, ushort>(ref Unsafe.Add(ref stateRef, 2));

        ref byte matrixPtr = ref Unsafe.Add(ref stateRef, MetadataSize);

        localI = (localI + 1) & 255;
        ref byte baseLvlI = ref Unsafe.Add(ref matrixPtr, localI << 4); // Layer 0 default
        localJ = (localJ + baseLvlI) & 255;
        ref byte baseLvlJ = ref Unsafe.Add(ref matrixPtr, localJ << 4);

        byte baseTemp = baseLvlI;
        baseLvlI = baseLvlJ;
        baseLvlJ = baseTemp;

        int dynamicLevels = 6 + (((localI ^ localJ) * 9) & 15);

        int currentIndexForLevel = (baseLvlI + baseLvlJ) & 255;
        byte userValue = Unsafe.Add(ref matrixPtr, currentIndexForLevel << 4);

        // --- 1. Forward Pass (READ-ONLY: Swaps Removed) ---
        int forwardStepMultiplier = 59;
        int targetLayer;
        ulong layerStackLow = 0, layerStackHigh = 0;

        for (int step = 1; step < dynamicLevels; step++)
        {
            targetLayer = (step ^ localI ^ userValue) & 15;

            if (step < 16) layerStackLow |= (ulong)targetLayer << (step << 2);
            else layerStackHigh |= (ulong)targetLayer << ((step - 16) << 2);

            // Pure read-only extraction path: combines lookups without matrix mutation writes
            int indexBitOffset = currentIndexForLevel << 4;
            int lvlJBitOffset = ((currentIndexForLevel ^ localI ^ forwardStepMultiplier) & 255) << 4;
            forwardStepMultiplier = (forwardStepMultiplier + 59) & 255;

            ref byte refI = ref Unsafe.Add(ref matrixPtr, indexBitOffset | targetLayer);
            ref byte refJ = ref Unsafe.Add(ref matrixPtr, lvlJBitOffset | targetLayer);

            currentIndexForLevel = (refI + refJ) & 255;
            userValue = Unsafe.Add(ref matrixPtr, (currentIndexForLevel << 4) | targetLayer);
        }

        // --- 2. Reverse Scrambling Pass (MUTATING: Swaps Kept Here) ---
        int secretI = (localI + 1) & 255;
        int secretJ = (localJ ^ userValue) & 255;
        byte erasureByte = Unsafe.Add(ref matrixPtr, (((secretI + secretJ) & 255) << 4));

        int reverseStepMultiplier = (dynamicLevels - 1) * 101;
        int constantPart = localJ + ((localJ >> 2) ^ 0xAA);
        int reverseI, reverseJ;

        for (int step = dynamicLevels - 1; step >= 0; step--)
        {
            if (step < 16) targetLayer = (int)((layerStackLow >> (step << 2)) & 15);
            else targetLayer = (int)((layerStackHigh >> ((step - 16) << 2)) & 15);

            reverseI = (currentIndexForLevel ^ erasureByte) & 255;
            reverseJ = (reverseI + constantPart + reverseStepMultiplier) & 255;
            reverseStepMultiplier -= 101;

            int revIBitOffset = reverseI << 4;
            int revJBitOffset = reverseJ << 4;

            // Perform the state-mutating array swaps on the way backward
            ref byte refRevI = ref Unsafe.Add(ref matrixPtr, revIBitOffset | targetLayer);
            ref byte refRevJ = ref Unsafe.Add(ref matrixPtr, revJBitOffset | targetLayer);

            byte temp = refRevI; refRevI = refRevJ; refRevJ = temp;
            erasureByte = (byte)(refRevI ^ reverseJ);
        }

        // Final output bit-mixer
        uint mix = ((uint)(userValue ^ (userValue >> 4)) * 31) & 0xFF;
        byte result = (byte)(mix ^ (mix >> 3));

        count++;

        Unsafe.Add(ref stateRef, 0) = (byte)localI;
        Unsafe.Add(ref stateRef, 1) = (byte)localJ;
        Unsafe.As<byte, ushort>(ref Unsafe.Add(ref stateRef, 2)) = count;

        return result;
    }

    /// <summary>
    /// Highly optimized array/span generation strategy.
    /// Inlines internal generation loop directly to avoid the overhead of individual NextByte calls.
    /// </summary>
    public void NextBytes(Span<byte> buffer)
    {
        if (buffer.IsEmpty) return;

        byte[] state = GetOrCreateState();
        ref byte stateRef = ref MemoryMarshal.GetReference((Span<byte>)state);

        int localI = Unsafe.Add(ref stateRef, 0);
        int localJ = Unsafe.Add(ref stateRef, 1);
        ushort count = Unsafe.As<byte, ushort>(ref Unsafe.Add(ref stateRef, 2));

        ref byte matrixPtr = ref Unsafe.Add(ref stateRef, MetadataSize);

        int targetLayer, reverseI, reverseJ;

        for (int i = 0; i < buffer.Length; i++)
        {
            localI = (localI + 1) & 255;
            ref byte baseLvlI = ref Unsafe.Add(ref matrixPtr, localI << 4); // Layer 0 default
            localJ = (localJ + baseLvlI) & 255;
            ref byte baseLvlJ = ref Unsafe.Add(ref matrixPtr, localJ << 4);

            byte baseTemp = baseLvlI;
            baseLvlI = baseLvlJ;
            baseLvlJ = baseTemp;

            int dynamicLevels = 6 + (((localI ^ localJ) * 9) & 15);

            int currentIndexForLevel = (baseLvlI + baseLvlJ) & 255;
            byte userValue = Unsafe.Add(ref matrixPtr, currentIndexForLevel << 4);

            // --- 1. Forward Cascade Loop (READ-ONLY: Swaps Removed) ---
            int forwardStepMultiplier = 59;
            ulong layerStackLow = 0;
            ulong layerStackHigh = 0;

            for (int step = 1; step < dynamicLevels; step++)
            {
                targetLayer = (step ^ localI ^ userValue) & 15;

                // Push target layer indices bitwise into registers
                if (step < 16)
                {
                    layerStackLow |= (ulong)targetLayer << (step << 2);
                }
                else
                {
                    layerStackHigh |= (ulong)targetLayer << ((step - 16) << 2);
                }

                int indexBitOffset = currentIndexForLevel << 4;
                int lvlJBitOffset = ((currentIndexForLevel ^ localI ^ forwardStepMultiplier) & 255) << 4;
                forwardStepMultiplier = (forwardStepMultiplier + 59) & 255;

                ref byte refI = ref Unsafe.Add(ref matrixPtr, indexBitOffset | targetLayer);
                ref byte refJ = ref Unsafe.Add(ref matrixPtr, lvlJBitOffset | targetLayer);

                currentIndexForLevel = (refI + refJ) & 255;
                userValue = Unsafe.Add(ref matrixPtr, (currentIndexForLevel << 4) | targetLayer);
            }

            // --- 2. Reverse Scrambling Pass (MUTATING: Swaps Kept Here) ---
            int secretI = (localI + 1) & 255;
            int secretJ = (localJ ^ userValue) & 255;
            byte erasureByte = Unsafe.Add(ref matrixPtr, (((secretI + secretJ) & 255) << 4));

            int reverseStepMultiplier = (dynamicLevels - 1) * 101;
            int constantPart = localJ + ((localJ >> 2) ^ 0xAA);

            for (int step = dynamicLevels - 1; step >= 0; step--)
            {
                // Pop layer configuration instantaneously from register stack
                if (step < 16)
                {
                    targetLayer = (int)((layerStackLow >> (step << 2)) & 15);
                }
                else
                {
                    targetLayer = (int)((layerStackHigh >> ((step - 16) << 2)) & 15);
                }

                reverseI = (currentIndexForLevel ^ erasureByte) & 255;
                reverseJ = (reverseI + constantPart + reverseStepMultiplier) & 255;
                reverseStepMultiplier -= 101;

                int revIBitOffset = reverseI << 4;
                int revJBitOffset = reverseJ << 4;

                ref byte refRevI = ref Unsafe.Add(ref matrixPtr, revIBitOffset | targetLayer);
                ref byte refRevJ = ref Unsafe.Add(ref matrixPtr, revJBitOffset | targetLayer);

                // Mutate layer configurations sequentially on backward execution path
                byte temp = refRevI; refRevI = refRevJ; refRevJ = temp;
                erasureByte = (byte)(refRevI ^ reverseJ);
            }

            // Final output bit-mixer (Secures exact compatibility mapping variants)
            uint mix = ((uint)(userValue ^ (userValue >> 4)) * 31) & 0xFF;
            buffer[i] = (byte)(mix ^ (mix >> 3));

            // Reseed interval handler across continuous span streaming operations
            count++;
            if (count >= ReSeedInterval)
            {
                // 1. Fetch quick entropy directly into local registers without matrix re-allocations
                // We only need 4 bytes to completely refresh our running variables
                uint freshEntropy = 0;

                // Create a 4-byte Span pointing directly to freshEntropy without an unsafe block
                Span<byte> entropySpan = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref freshEntropy, 1));
                RandomNumberGenerator.Fill(entropySpan);

                // 2. Perturb running counters directly using the fresh entropy
                localI = (localI ^ (int)(freshEntropy & 0xFF)) & 255;
                localJ = (localJ ^ (int)((freshEntropy >> 8) & 0xFF)) & 255;

                // 3. Shake up the base matrix layer position lightly using the remaining entropy bits
                int shakeIndex = (int)((freshEntropy >> 16) & 255);
                Unsafe.Add(ref matrixPtr, shakeIndex << 4) ^= (byte)(freshEntropy >> 24);

                count = 0; // Reset the streaming interval back to zero cleanly
            }
        }

        // Single structured write back to the active state block upon completing the array fill
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

        // Fast-range selection: multiplication instead of modulo
        uint x = NextUInt32();
        ulong m = (ulong)x * (ulong)range;
        uint l = (uint)m;

        // Rare rejection case for absolute uniformity
        if (l < range)
        {
            uint t = unchecked((uint)-(int)range) % range;
            while (l < t)
            {
                x = NextUInt32();
                m = (ulong)x * (ulong)range;
                l = (uint)m;
            }
        }

        // Explicitly cast the shifted ulong to uint to fix ambiguity
        return (int)(minValue + (uint)(m >> 32));
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
        for (int layer = 0; layer < 16; layer++)
        {
            //int offset = MetadataSize + (layer << 8);
            for (int val = 0; val < 256; val++)
            {
                Unsafe.Add(ref matrixRef, (val << 4) | layer) = (byte)val;
                //Unsafe.Add(ref matrixRef, offset + val) = (byte)val;
            }
        }

        // 2. Fetch a single heavy cryptographic entropy buffer from the OS
        // 1024 bytes completely isolates 5 independent shuffle tracks
        Span<byte> heavyChaos = stackalloc byte[1024];
        RandomNumberGenerator.Fill(heavyChaos);

        // Seed initial state registers with pristine randomness
        Unsafe.Add(ref matrixRef, 0) = 0; // localI start
        Unsafe.Add(ref matrixRef, 1) = 0; // localJ start
        Unsafe.As<byte, ushort>(ref Unsafe.Add(ref matrixRef, 2)) = 0; // count start

        // 3. Perform completely unbiased Fisher-Yates layer shuffling
        // Uses a fast bit-mask to completely bypass modulo remainder bias
        int chaosPointer = 2;
        for (int layer = 0; layer < 16; layer++)
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
                    j = (heavyChaos[chaosPointer & 1023] ^ layer) & powerOfTwoMask;
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