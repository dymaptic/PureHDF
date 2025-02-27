namespace PureHDF.Selections;

internal record DecodeInfo<T>(
    ulong[] SourceDims,
    ulong[] SourceChunkDims,
    ulong[] TargetDims,
    ulong[] TargetChunkDims,
    Selection SourceSelection,
    Selection TargetSelection,
    Func<ulong, IH5ReadStream> GetSourceStream,
    DecodeDelegate<T> Decoder,
    int SourceTypeSize,
    int TargetTypeSizeFactor,
    bool AllowBulkCopy
);

internal record EncodeInfo<T>(
    ulong[] SourceDims,
    ulong[] SourceChunkDims,
    ulong[] TargetDims,
    ulong[] TargetChunkDims,
    Selection SourceSelection,
    Selection TargetSelection,
    Func<ulong, Memory<T>> GetSourceBuffer,
    Func<ulong, IH5WriteStream> GetTargetStream,
    EncodeDelegate<T> Encoder,
    int SourceTypeSizeFactor,
    int TargetTypeSize,
    bool AllowBulkCopy
);

internal readonly record struct RelativeStep(
    ulong ChunkIndex,
    ulong Offset,
    ulong Length
);

internal static class SelectionHelper
{
    public static IEnumerable<RelativeStep> Walk(
        int rank,
        ulong[] dims,
        ulong[] chunkDims,
        Selection selection,
        bool allowBulkCopy
    )
    {
        /* check if there is anything to do */
        if (selection.TotalElementCount == 0)
            yield break;

        /* validate rank */
        if (dims.Length != rank || chunkDims.Length != rank)
            throw new RankException($"The length of each array parameter must match the rank parameter.");

        /* check for bulk copy (short cut) */
        if (allowBulkCopy &&
            chunkDims.SequenceEqual(dims) /* single chunk or not chunked at all */ &&
            selection is HyperslabSelection hs &&
            hs.Starts.All(start => start == 0) &&
            hs.Strides.SequenceEqual(hs.Blocks)
        )
        {
            var canBulkCopy = true;

            for (int i = 0; i < rank; i++)
            {
                if (hs.Blocks[i] * hs.Counts[i] != dims[i])
                    canBulkCopy = false;
            }

            if (canBulkCopy)
            {
                var relativeStep = new RelativeStep(
                    ChunkIndex: 0,
                    Offset: 0,
                    Length: selection.TotalElementCount
                );

                yield return relativeStep;
                yield break;
            }
        }

        /* prepare some useful arrays */
        var lastDim = rank - 1;
        var chunkLength = chunkDims.Aggregate(1UL, (x, y) => x * y);

        /* prepare last dimension variables */
        var lastChunkDim = chunkDims[lastDim];

        /* Prepare efficient conversion from coordinates to linear index */
        var scaledOffsets = new ulong[rank];

        var scaledDims = dims
            .Select((dim, i) => MathUtils.CeilDiv(dim, chunkDims[i]))
            .ToArray();

        var downChunkCounts = scaledDims.AccumulateReverse();
        var chunkIndex = 0UL;

        /* walk */
        foreach (var step in selection.Walk(limits: dims))
        {
            /* validate rank */
            if (step.Coordinates.Length != rank)
                throw new RankException($"The length of the step coordinates array must match the rank parameter.");

            var remaining = step.ElementCount;

            /* process slice */
            while (remaining > 0)
            {
                Span<ulong> chunkOffsets = stackalloc ulong[rank];
                var hasChunkIndexChanged = false;

                for (int dimension = 0; dimension < rank; dimension++)
                {
                    var newScaledOffset = step.Coordinates[dimension] / chunkDims[dimension];

                    if (!hasChunkIndexChanged && scaledOffsets[dimension] != newScaledOffset)
                        hasChunkIndexChanged = true;

                    scaledOffsets[dimension] = newScaledOffset;
                    chunkOffsets[dimension] = step.Coordinates[dimension] % chunkDims[dimension];
                }

                if (hasChunkIndexChanged)
                    chunkIndex = scaledOffsets.ToLinearIndexPrecomputed(downChunkCounts);

                var offset = chunkOffsets.ToLinearIndex(chunkDims);
                var currentLength = Math.Min(lastChunkDim - chunkOffsets[lastDim], remaining);

                yield return new RelativeStep()
                {
                    ChunkIndex = chunkIndex,
                    Offset = offset,
                    Length = currentLength
                };

                remaining -= currentLength;
                step.Coordinates[lastDim] += currentLength;
            }
        }
    }

    public static void Encode<TSource>(
        int sourceRank,
        int targetRank,
        EncodeInfo<TSource> encodeInfo)
    {
        /* validate selections */
        if (encodeInfo.SourceSelection.TotalElementCount != encodeInfo.TargetSelection.TotalElementCount)
            throw new ArgumentException("The lengths of the source selection and target selection are not equal.");

        /* validate rank of dims */
        if (encodeInfo.SourceDims.Length != sourceRank ||
            encodeInfo.SourceChunkDims.Length != sourceRank ||
            encodeInfo.TargetDims.Length != targetRank ||
            encodeInfo.TargetChunkDims.Length != targetRank)
            throw new RankException($"The length of each array parameter must match the rank parameter.");

        /* walkers */
        var sourceWalker = Walk(
            sourceRank,
            encodeInfo.SourceDims,
            encodeInfo.SourceChunkDims,
            encodeInfo.SourceSelection,
            encodeInfo.AllowBulkCopy
        ).GetEnumerator();

        var targetWalker = Walk(
            targetRank,
            encodeInfo.TargetDims,
            encodeInfo.TargetChunkDims,
            encodeInfo.TargetSelection,
            encodeInfo.AllowBulkCopy
        ).GetEnumerator();

        /* select method */
        EncodeStream(sourceWalker, targetWalker, encodeInfo);
    }

    private static void EncodeStream<TResult>(
        IEnumerator<RelativeStep> sourceWalker,
        IEnumerator<RelativeStep> targetWalker,
        EncodeInfo<TResult> encodeInfo)
    {
        /* initialize source walker */
        var sourceBuffer = default(Memory<TResult>);
        var lastSourceChunkIndex = 0UL;
        var currentSource = default(Memory<TResult>);

        /* initialize target walker */
        var targetStream = default(IH5WriteStream);
        var lastTargetChunkIndex = 0UL;

        /* walk until end */
        while (targetWalker.MoveNext())
        {
            /* load next target stream */
            var targetStep = targetWalker.Current;

            if (targetStream is null /* if stream not assigned yet */ ||
                targetStep.ChunkIndex != lastTargetChunkIndex /* or the chunk has changed */)
            {
                targetStream = encodeInfo.GetTargetStream(targetStep.ChunkIndex);
                lastTargetChunkIndex = targetStep.ChunkIndex;
            }

            var currentOffset = (long)targetStep.Offset;
            var currentLength = (int)targetStep.Length;

            while (currentLength > 0)
            {
                /* load next source buffer */
                if (currentSource.Length == 0)
                {
                    var success = sourceWalker.MoveNext();
                    var sourceStep = sourceWalker.Current;

                    if (!success || sourceStep.Length == 0)
                        throw new FormatException("The source walker stopped early.");

                    if (sourceBuffer.Length == 0 /* if buffer not assigned yet */ ||
                        sourceStep.ChunkIndex != lastSourceChunkIndex /* or the chunk has changed */)
                    {
                        sourceBuffer = encodeInfo.GetSourceBuffer(sourceStep.ChunkIndex);
                        lastSourceChunkIndex = sourceStep.ChunkIndex;
                    }

                    currentSource = sourceBuffer.Slice(
                        (int)sourceStep.Offset * encodeInfo.SourceTypeSizeFactor,
                        (int)sourceStep.Length * encodeInfo.SourceTypeSizeFactor);
                }

                /* copy */
                var length = Math.Min(currentLength, currentSource.Length);
                var sourceLength = length * encodeInfo.SourceTypeSizeFactor;

                targetStream.Seek(currentOffset * encodeInfo.TargetTypeSize, SeekOrigin.Begin);

                encodeInfo.Encoder(
                    currentSource[..sourceLength],
                    targetStream);

                currentOffset += length;
                currentLength -= length;
                currentSource = currentSource[sourceLength..];
            }
        }
    }

    public static void Decode<TResult>(
        int sourceRank,
        int targetRank,
        DecodeInfo<TResult> decodeInfo,
        Span<TResult> targetBuffer)
    {
        /* validate selections */
        if (decodeInfo.SourceSelection.TotalElementCount != decodeInfo.TargetSelection.TotalElementCount)
            throw new ArgumentException("The lengths of the source selection and target selection are not equal.");

        /* validate rank of dims */
        if (decodeInfo.SourceDims.Length != sourceRank ||
            decodeInfo.SourceChunkDims.Length != sourceRank ||
            decodeInfo.TargetDims.Length != targetRank ||
            decodeInfo.TargetChunkDims.Length != targetRank)
            throw new RankException($"The length of each array parameter must match the rank parameter.");

        /* walkers */
        var sourceWalker = Walk(
            sourceRank,
            decodeInfo.SourceDims,
            decodeInfo.SourceChunkDims,
            decodeInfo.SourceSelection,
            decodeInfo.AllowBulkCopy
        ).GetEnumerator();

        var targetWalker = Walk(
            targetRank,
            decodeInfo.TargetDims,
            decodeInfo.TargetChunkDims,
            decodeInfo.TargetSelection,
            decodeInfo.AllowBulkCopy
        ).GetEnumerator();

        /* select method */
        DecodeStream(sourceWalker, targetWalker, decodeInfo, targetBuffer);
    }

    private static void SeekTo(IH5ReadStream stream, ulong position)
    {
        int fullChunks = (int)(position / int.MaxValue);

        for (int i = 0; i < fullChunks; i++)
        {
            stream.Seek(int.MaxValue, SeekOrigin.Current);
        }

        int remainder = (int)(position % int.MaxValue);
        stream.Seek(remainder, SeekOrigin.Current);
    }

    private static void DecodeStream<TResult>(
        IEnumerator<RelativeStep> sourceWalker,
        IEnumerator<RelativeStep> targetWalker,
        DecodeInfo<TResult> decodeInfo,
        Span<TResult> targetBuffer)
    {
        /* initialize source walker */
        var sourceStream = default(IH5ReadStream);
        var lastSourceChunkIndex = 0UL;

        /* initialize target walker */
        var currentTarget = default(Span<TResult>);

        /* walk until end */
        while (sourceWalker.MoveNext())
        {
            /* load next source stream */
            var sourceStep = sourceWalker.Current;

            if (sourceStream is null /* if stream not assigned yet */ ||
                sourceStep.ChunkIndex != lastSourceChunkIndex /* or the chunk has changed */)
            {
                sourceStream = decodeInfo.GetSourceStream(sourceStep.ChunkIndex);
                lastSourceChunkIndex = sourceStep.ChunkIndex;
            }

            var virtualDatasetStream = sourceStream as VirtualDatasetStream<TResult>;
            var currentOffset = sourceStep.Offset;
            var currentLength = (int)sourceStep.Length;

            while (currentLength > 0)
            {
                /* load next target buffer */
                if (currentTarget.Length == 0)
                {
                    var success = targetWalker.MoveNext();
                    var targetStep = targetWalker.Current;

                    if (!success || targetStep.Length == 0)
                        throw new FormatException("The target walker stopped early.");

                    currentTarget = targetBuffer.Slice(
                        (int)targetStep.Offset * decodeInfo.TargetTypeSizeFactor,
                        (int)targetStep.Length * decodeInfo.TargetTypeSizeFactor);
                }

                /* copy */
                var length = Math.Min(currentLength, currentTarget.Length);
                var targetLength = length * decodeInfo.TargetTypeSizeFactor;

                if (virtualDatasetStream is null)
                {
                    // TODO: I think we need to do a looped seek here where we do a relative seek until we have gotten to
                    // where we want instead of an aboslute seek where currentOffset * decodeInfo.SourceTypeSize overflows the `long` type.
                    //sourceStream.Seek(currentOffset * decodeInfo.SourceTypeSize, SeekOrigin.Begin);
                    sourceStream.Seek(0, SeekOrigin.Begin);
                    for (int i=1; i<=decodeInfo.SourceTypeSize; i++)
                    {
                        SeekTo(sourceStream, currentOffset);
                        //sourceStream.Seek(currentOffset, SeekOrigin.Current);
                    }

                    decodeInfo.Decoder(
                        sourceStream,
                        currentTarget[..targetLength]);
                }

                else
                {
                    virtualDatasetStream.Seek(0, SeekOrigin.Begin);
                    //virtualDatasetStream.Seek(currentOffset, SeekOrigin.Begin);
                    SeekTo(virtualDatasetStream, currentOffset);

                    virtualDatasetStream.ReadVirtual(
                        currentTarget[..targetLength]);
                }

                currentOffset += (ulong)length;
                currentLength -= length;
                currentTarget = currentTarget[targetLength..];
            }
        }
    }
}