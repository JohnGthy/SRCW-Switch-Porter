// BC7 block decoder taken from CUE4Parse (https://github.com/FabianFG/CUE4Parse),
// CUE4Parse-Conversion/Textures/BC/BC7.cs and BC7Tables.cs. Licensed under the Apache License 2.0
// (see licenses/CUE4Parse-Apache-2.0.txt). Modified: trimmed to the block decoder, CommunityToolkit casts
// replaced with MemoryMarshal.Cast, namespace changed.
using System;
using System.Numerics;
using System.Numerics.Tensors;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SwitchPorter.ThirdParty;

internal static class Bc7Decoder
{
    static uint Interpolate(uint a, uint b, int weight) => (uint)((((int)a << 6) + ((int)b - (int)a) * weight + 32) >> 6);

    public static void DecodeBC7Block(UInt128 compressedBlock, Span<uint> decompressedBlock)
    {
        Span<byte> rgba = stackalloc byte[8 * 4];
        var rgb = rgba[..24];
        var alpha = rgba[24..];

        var dword = (uint) compressedBlock;
		int zeros = BitOperations.TrailingZeroCount(dword);
		int pos = zeros >= 8 ? 8 : zeros + 1;
		int mode = int.Min(zeros, 8);

        if (mode >= 8)
        {
            decompressedBlock.Clear();
            return;
        }

        int partition = 0;
        int numPartitions = 1;
        int rotation = 0;
        int indexSelectionBit = 0;

        switch (mode)
        {
            case 0:
                numPartitions = 3;
                partition = (int) (dword >> pos) & 0xF;
                pos += 4;
                break;
            case 1 or 3 or 7:
                numPartitions = 2;
                partition = (int) (dword >> pos) & 0x3F;
                pos += 6;
                break;
            case 2:
                numPartitions = 3;
                partition = (int) (dword >> pos) & 0x3F;
                pos += 6;
                break;
            case 4:
                rotation = (int) (dword >> pos) & 0x3;
                pos += 2;
                indexSelectionBit = (int) (dword >> pos++) & 1;
                break;
            case 5:
                rotation = (int) (dword >> pos) & 0x3;
                pos += 2;
                break;
        }

        int numEndpoints = numPartitions * 2;

        // Extract endpoints
        var colorBits = BC7Tables.ActualBitsCount[0][mode];
        var colorMask = (1u << colorBits) - 1;
        var stride = colorBits * numEndpoints;
        for (int i = 0; i < 3; ++i)
        {
            var l = i << 3;
            var data = (uint) (compressedBlock >> pos);
            for (int j = 0; j < numEndpoints; ++j)
            {
                rgb[l++] = (byte) (data & colorMask);
                data >>= colorBits;
            }

            pos += stride;
        }

        var alphaBits = BC7Tables.ActualBitsCount[1][mode];
        if (alphaBits > 0)
        {
            var data = (uint) (compressedBlock >> pos);
            var alphaMask = (1u << alphaBits) - 1;
            for (int j = 0; j < numEndpoints; ++j)
            {
                alpha[j] = (byte) (data & alphaMask);
                data >>= alphaBits;
            }
            pos += alphaBits * numEndpoints;
        }

        // Fully decode endpoints
        if (mode is 0 or 1 or 3 or 6 or 7)
        {
            TensorPrimitives.ShiftLeft(rgba, 1, rgba);

            if (mode == 1)
            {
                // if P-bit is shared
                var pMask = (uint) BC7Tables.Mode1PMask[(int)(compressedBlock >> pos) & 0x3];
                pos += 2;
                var span = MemoryMarshal.Cast<byte, uint>(rgba);
                span[0] |= pMask;
                span[2] |= pMask;
                span[4] |= pMask;
                span[6] |= pMask;
            }
            else
            {
                // unique P-bit per endpoint
                var pdata = (ulong)(compressedBlock >> pos) & ((1u << numEndpoints) - 1);
                pos += numEndpoints;
                var pMask = 0UL;
                for (int i = 0; i < numEndpoints; ++i)
                {
                    pMask |= (pdata & 1) << (i * 8);
                    pdata >>= 1;
                }

                var span = MemoryMarshal.Cast<byte, ulong>(rgba);
                span[0] |= pMask;
                span[1] |= pMask;
                span[2] |= pMask;
                span[3] |= pMask;
            }
        }

		var temp = MemoryMarshal.Cast<uint, byte>(decompressedBlock)[..24];
        int s = colorBits + ((BC7Tables.ModeHasPBits >> mode) & 1);
        TensorPrimitives.ShiftLeft(rgb, 8 - s, rgb);
        TensorPrimitives.ShiftRightLogical(rgb, s, temp);
        TensorPrimitives.BitwiseOr(rgb, temp, rgb);

        if (alphaBits == 0)
        {
			for (int i = 0; i < numEndpoints; ++i)
			{
				alpha[i] = 0xFF;
			}
        }
        else
        {
            s = alphaBits + ((BC7Tables.ModeHasPBits >> mode) & 1);
            for (int i = 0; i < numEndpoints; ++i)
			{
				alpha[i] <<= (8 - s);
                alpha[i] |= (byte)(alpha[i] >> s);
			}
        }

        int indexBits = mode switch
        {
            0 or 1 => 3,
            6 => 4,
            _ => 2
        };
        int indexBits2 = mode switch
        {
            4 => 3,
            5 => 2,
            _ => 0
        };
        ReadOnlySpan<int> weights = indexBits switch
        {
            2 => BC7Tables.AWeight2,
            3 => BC7Tables.AWeight3,
            _ => BC7Tables.AWeight4
        };

        [MethodImpl(MethodImplOptions.AggressiveOptimization | MethodImplOptions.AggressiveInlining)]
        uint FastInterpolate(int a, int b, int weight)
        {
            return (uint) (a + b * weight) >> 6;
        }

		if (numPartitions == 1)
        {
            // precalculate for fast interpolate
			var (r0, r1) = ((rgba[ 0] << 6) + 32, rgba[ 1] - rgba[ 0]);
			var (g0, g1) = ((rgba[ 8] << 6) + 32, rgba[ 9] - rgba[ 8]);
			var (b0, b1) = ((rgba[16] << 6) + 32, rgba[17] - rgba[16]);
			var (a0, a1) = ((rgba[24] << 6) + 32, rgba[25] - rgba[24]);

            var data = (ulong) (compressedBlock >> pos);
            var mask = (1u << indexBits) - 1;
            var data2 = (ulong) (compressedBlock >> (pos + 16 * indexBits - 1));
            var mask2 = (1u << indexBits2) - 1;
            ReadOnlySpan<int> weights2 = indexBits2 == 2 ? BC7Tables.AWeight2 : BC7Tables.AWeight3;
            for (int i = 0; i < 16; ++i)
            {
                uint r, g, b, a;
                var anchor = (i - 1) >>> 31;
                var weight = weights[(int)(data & (mask >> anchor))];
                data >>= indexBits - anchor;

                if (indexBits2 == 0)
                {
                    r = FastInterpolate(r0, r1, weight);
                    g = FastInterpolate(g0, g1, weight);
                    b = FastInterpolate(b0, b1, weight);
                    a = FastInterpolate(a0, a1, weight);
                }
                else
                {
                    var anchor2 = (i - 1) >>> 31;
                    var weight2 = weights2[(int)(data2 & (mask2 >> anchor2))];
                    data2 >>= indexBits2 - anchor2;
                    if (indexSelectionBit == 0)
                    {
                        r = FastInterpolate(r0, r1, weight);
                        g = FastInterpolate(g0, g1, weight);
                        b = FastInterpolate(b0, b1, weight);
                        a = FastInterpolate(a0, a1, weight2);
                    }
                    else
                    {
                        r = FastInterpolate(r0, r1, weight2);
                        g = FastInterpolate(g0, g1, weight2);
                        b = FastInterpolate(b0, b1, weight2);
                        a = FastInterpolate(a0, a1, weight);
                    }

                    switch (rotation)
                    {
                        case 1:
                            (a, r) = (r, a);
                            break;
                        case 2:
                            (a, g) = (g, a);
                            break;
                        case 3:
                            (a, b) = (b, a);
                            break;
                    }
                }

                decompressedBlock[i] = r | g << 8 | b << 16 | a << 24;
            }
        }
        else
        {
            var partitionTable = BC7Tables.PartitionSets[numPartitions - 2][partition];
            var data = (ulong) (compressedBlock >> pos);
            var mask = (1u << indexBits) - 1;
            for (int i = 0; i < 16; ++i)
            {
                int partitionSet = partitionTable[i];
                int anchor = partitionSet >> 7;
                var weight = weights[(int)(data & (mask >> anchor))];
                data >>= indexBits - anchor;
                var p = (partitionSet & 0x03) * 2;

                var r = Interpolate(rgba[p +  0], rgba[p +  1], weight);
                var g = Interpolate(rgba[p +  8], rgba[p +  9], weight);
                var b = Interpolate(rgba[p + 16], rgba[p + 17], weight);
                var a = Interpolate(rgba[p + 24], rgba[p + 25], weight);

                decompressedBlock[i] = r | g << 8 | b << 16 | a << 24;
            }
        }
    }
}
