// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using UnityEngine;
using UnityEngine.ParticleSystemJobs;

namespace Modstoke.Jobs;

/// <summary>Borrowed views of the native particle channels for the current job callback.</summary>
/// <remarks>
/// Views are valid only during the callback and require no disposal. Do not retain them or
/// let their storage escape through unsafe pointers. Unavailable channels expose empty spans.
/// </remarks>
public readonly unsafe ref partial struct ParticleJobData
{
    private readonly NativeParticleData _data;

    internal ParticleJobData(scoped in NativeParticleData data)
    {
        _data = data;
    }

    public int Count => _data.count;
    public ParticleVector3Data Positions => new(in _data.positions, Count);
    public ParticleVector3Data Velocities => new(in _data.velocities, Count);
    public ParticleVector3Data AxisOfRotations => new(in _data.axisOfRotations, Count);
    public ParticleVector3Data Rotations => new(in _data.rotations, Count);
    public ParticleVector3Data RotationalSpeeds => new(in _data.rotationalSpeeds, Count);
    public ParticleVector3Data Sizes => new(in _data.sizes, Count);
    public Span<Color32> StartColors => Borrow<Color32>(_data.startColors, Count);
    public Span<float> AliveTimePercent => Borrow<float>(_data.aliveTimePercent, Count);
    public Span<float> InverseStartLifetimes => Borrow<float>(_data.inverseStartLifetimes, Count);
    public Span<uint> RandomSeeds => Borrow<uint>(_data.randomSeeds, Count);
    public ParticleVector4Data CustomData1 => new(in _data.customData1, Count);
    public ParticleVector4Data CustomData2 => new(in _data.customData2, Count);
    public Span<int> MeshIndices => Borrow<int>(_data.meshIndices, Count);

    private static Span<T> Borrow<T>(IntPtr pointer, int length) where T : unmanaged
    {
        return pointer == IntPtr.Zero || length == 0
            ? Span<T>.Empty
            : new Span<T>(pointer.ToPointer(), length);
    }
}

/// <summary>A borrowed, component-wise view of a native three-component particle channel.</summary>
/// <remarks>
/// Valid only during the job callback; no disposal is required. Unavailable components are
/// empty. Do not retain these views or let their storage escape through unsafe pointers.
/// </remarks>
public readonly ref struct ParticleVector3Data
{
    internal unsafe ParticleVector3Data(scoped in NativeParticleData.Array3 data, int count)
    {
        Count = count;
        X = data.x == IntPtr.Zero || count == 0 ? Span<float>.Empty : new Span<float>(data.x.ToPointer(), count);
        Y = data.y == IntPtr.Zero || count == 0 ? Span<float>.Empty : new Span<float>(data.y.ToPointer(), count);
        Z = data.z == IntPtr.Zero || count == 0 ? Span<float>.Empty : new Span<float>(data.z.ToPointer(), count);
    }

    public Span<float> X { get; }
    public Span<float> Y { get; }
    public Span<float> Z { get; }
    public int Count { get; }
    public bool IsAvailable => X.Length == Count && Y.Length == Count && Z.Length == Count;

    public Vector3 this[int index] => new Vector3 { x = X[index], y = Y[index], z = Z[index] };

    /// <summary>Writes all components after validating that the index exists in each one.</summary>
    public void Set(int index, Vector3 value)
    {
        if ((uint)index >= (uint)X.Length || (uint)index >= (uint)Y.Length ||
            (uint)index >= (uint)Z.Length)
        {
            throw new IndexOutOfRangeException();
        }

        X[index] = value.x;
        Y[index] = value.y;
        Z[index] = value.z;
    }
}

/// <summary>A borrowed, component-wise view of a native four-component particle channel.</summary>
/// <remarks>
/// Valid only during the job callback; no disposal is required. Unavailable components are
/// empty. Do not retain these views or let their storage escape through unsafe pointers.
/// </remarks>
public readonly ref struct ParticleVector4Data
{
    internal unsafe ParticleVector4Data(scoped in NativeParticleData.Array4 data, int count)
    {
        Count = count;
        X = data.x == IntPtr.Zero || count == 0 ? Span<float>.Empty : new Span<float>(data.x.ToPointer(), count);
        Y = data.y == IntPtr.Zero || count == 0 ? Span<float>.Empty : new Span<float>(data.y.ToPointer(), count);
        Z = data.z == IntPtr.Zero || count == 0 ? Span<float>.Empty : new Span<float>(data.z.ToPointer(), count);
        W = data.w == IntPtr.Zero || count == 0 ? Span<float>.Empty : new Span<float>(data.w.ToPointer(), count);
    }

    public Span<float> X { get; }
    public Span<float> Y { get; }
    public Span<float> Z { get; }
    public Span<float> W { get; }
    public int Count { get; }
    public bool IsAvailable => X.Length == Count && Y.Length == Count && Z.Length == Count && W.Length == Count;

    public Vector4 this[int index] => new Vector4 { x = X[index], y = Y[index], z = Z[index], w = W[index] };

    /// <summary>Writes all components after validating that the index exists in each one.</summary>
    public void Set(int index, Vector4 value)
    {
        if ((uint)index >= (uint)X.Length || (uint)index >= (uint)Y.Length ||
            (uint)index >= (uint)Z.Length || (uint)index >= (uint)W.Length)
        {
            throw new IndexOutOfRangeException();
        }

        X[index] = value.x;
        Y[index] = value.y;
        Z[index] = value.z;
        W[index] = value.w;
    }
}
