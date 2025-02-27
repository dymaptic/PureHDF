﻿using System.Collections;
using System.Reflection;
using Xunit;

namespace PureHDF.Tests.Writing;

public partial class DatasetTests
{
    public static IList<object[]> CommonData { get; } = WritingTestData.Common;
    public static IList<object[]> CommonData_FixedLengthString { get; } = WritingTestData.Common_FixedLengthString;
    public static IList<object[]> CommonData_FixedLengthStringMapper { get; } = WritingTestData.Common_FixedLengthStringMapper;

    [Theory]
    [MemberData(nameof(CommonData))]
    public void CanWrite_Common(object data)
    {
        // Arrange
        var type = data.GetType();

        var file = new H5File
        {
            [type.Name] = data
        };

        var filePath = Path.GetTempFileName();

        static string? fieldNameMapper(FieldInfo fieldInfo)
        {
            var attribute = fieldInfo.GetCustomAttribute<H5NameAttribute>();
            return attribute is not null ? attribute.Name : default;
        }

        static string? propertyNameMapper(PropertyInfo propertyInfo)
        {
            var attribute = propertyInfo.GetCustomAttribute<H5NameAttribute>();
            return attribute is not null ? attribute.Name : default;
        }

        var options = new H5WriteOptions(
            IncludeStructProperties: type == typeof(WritingTestRecordStruct) || type == typeof(Dictionary<string, int>[]),
            FieldNameMapper: fieldNameMapper,
            PropertyNameMapper: propertyNameMapper
        );

        // Act
        file.Write(filePath, options);

        // Assert
        try
        {
            /* utf-8 is base8 encoded: https://stackoverflow.com/questions/75174726/hdf5-how-to-decode-utf8-encoded-string-from-h5dump-output*/
            var actual = TestUtils.DumpH5File(filePath);

            var suffix = type switch
            {
                Type when
                    type == typeof(bool)
                    => $"_{data}",

                Type when
                    typeof(IDictionary).IsAssignableFrom(type)
                    => $"_{type.GenericTypeArguments[0].Name}_{type.GenericTypeArguments[1].Name}",

                Type when type !=
                    typeof(string) &&
                    typeof(IEnumerable).IsAssignableFrom(type) &&
                    !type.IsArray
                    => $"_{type.GenericTypeArguments[0].Name}",

                Type when
                    type.IsGenericType &&
                    typeof(Memory<>).Equals(type.GetGenericTypeDefinition())
                    => $"_{type.GenericTypeArguments[0].Name}",

                _ => default
            };

            var expected = File
                .ReadAllText($"DumpFiles/data_{type.Name}{suffix}.dump")
                .Replace("<file-path>", filePath)
                .Replace("<type>", "DATASET");

            Assert.Equal(expected, actual);
        }
        finally
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
        }
    }

    [Theory]
    [MemberData(nameof(CommonData_FixedLengthString))]
    public void CanWrite_Common_DefaultFixedLengthString(object data)
    {
        // Arrange
        var type = data.GetType();

        var file = new H5File
        {
            [type.Name] = data
        };

        var filePath = Path.GetTempFileName();

        var options = new H5WriteOptions(
            DefaultStringLength: 6
        );

        // Act
        file.Write(filePath, options);

        // Assert
        try
        {
            /* utf-8 is base8 encoded: https://stackoverflow.com/questions/75174726/hdf5-how-to-decode-utf8-encoded-string-from-h5dump-output*/
            var actual = TestUtils.DumpH5File(filePath);

            var expected = File
                .ReadAllText($"DumpFiles/data_default_fls_{type.Name}.dump")
                .Replace("<file-path>", filePath)
                .Replace("<type>", "DATASET");

            Assert.Equal(expected, actual);
        }
        finally
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
        }
    }

    [Theory]
    [MemberData(nameof(CommonData_FixedLengthStringMapper))]
    public void CanWrite_Common_FixedLengthStringMapper(object data)
    {
        // Arrange
        var type = data.GetType();

        var file = new H5File
        {
            [type.Name] = data
        };

        var filePath = Path.GetTempFileName();

        static int? fieldStringLengthMapper(FieldInfo fieldInfo)
        {
            var attribute = fieldInfo.GetCustomAttribute<H5StringLengthAttribute>();
            return attribute is not null ? attribute.Length : default;
        }

        static int? propertyStringLengthMapper(PropertyInfo propertyInfo)
        {
            var attribute = propertyInfo.GetCustomAttribute<H5StringLengthAttribute>();
            return attribute is not null ? attribute.Length : default;
        }

        var options = new H5WriteOptions(
            DefaultStringLength: 3,
            FieldStringLengthMapper: fieldStringLengthMapper,
            PropertyStringLengthMapper: propertyStringLengthMapper
        );

        // Act
        file.Write(filePath, options);

        // Assert
        try
        {
            /* utf-8 is base8 encoded: https://stackoverflow.com/questions/75174726/hdf5-how-to-decode-utf8-encoded-string-from-h5dump-output*/
            var actual = TestUtils.DumpH5File(filePath);

            var expected = File
                .ReadAllText($"DumpFiles/data_default_flsm_{type.Name}.dump")
                .Replace("<file-path>", filePath)
                .Replace("<type>", "DATASET");

            Assert.Equal(expected, actual);
        }
        finally
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
        }
    }

    [Theory]
    [InlineData([1])]
    [InlineData([null])]
    public void CanWrite_NullableValueType_Scalar(int? data)
    {
        // Arrange
        var file = new H5File();

        file["Nullable`1"] = new H5Dataset<int?>(data);

        var filePath = Path.GetTempFileName();

        // Act
        file.Write(filePath);

        // Assert
        try
        {
            var actual = TestUtils.DumpH5File(filePath);
            var suffix = data is null ? "null" : "value";

            var expected = File
                .ReadAllText($"DumpFiles/data_Nullable`1_Int32_{suffix}.dump")
                .Replace("<file-path>", filePath)
                .Replace("<type>", "DATASET");

            Assert.Equal(expected, actual);
        }
        finally
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
        }
    }

    [Fact]
    public void CanWrite_NullableValueType_Array()
    {
        // Arrange
        var file = new H5File();

        var data = new int?[]
        {
            1,
            null,
            -1
        };

        var type = data.GetType();

        file[type.Name] = data;

        var filePath = Path.GetTempFileName();

        // Act
        file.Write(filePath);

        // Assert
        try
        {
            var actual = TestUtils.DumpH5File(filePath);

            var expected = File
                .ReadAllText($"DumpFiles/data_Nullable`1_Int32[].dump")
                .Replace("<file-path>", filePath)
                .Replace("<type>", "DATASET");

            Assert.Equal(expected, actual);
        }
        finally
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
        }
    }

    [Fact]
    /* this test ensures that the global heap collection is also written to disk in deferred mode */
    public void CanWrite_VariableLength_Deferred()
    {
        // Arrange
        var file = new H5File();

        int[][] data =
        [
            [1, 2, 3],
            [1, 2]
        ];

        var type = data.GetType();
        var dataset = new H5Dataset<int[][]>([(ulong)data.Length]);

        file[type.Name] = dataset;

        var filePath = Path.GetTempFileName();

        // Act
        using (var writer = file.BeginWrite(filePath))
        {
            writer.Write(dataset, data);
        }

        // Assert
        try
        {
            var actual = TestUtils.DumpH5File(filePath);

            var expected = File
                .ReadAllText($"DumpFiles/data_Int32[][]_deferred.dump")
                .Replace("<file-path>", filePath)
                .Replace("<type>", "DATASET");

            Assert.Equal(expected, actual);
        }
        finally
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
        }
    }

    [Fact]
    public void CanWrite_Anonymous()
    {
        // Arrange
        var file = new H5File();

        var data = new
        {
            Numerical = 1,
            Boolean = true,
            Enum = FileAccess.Read,
            Anonymous = new
            {
                A = 1,
                B = 9.81
            }
        };

        var type = data.GetType();

        file[type.Name] = data;

        var filePath = Path.GetTempFileName();

        // Act
        file.Write(filePath);

        // Assert
        try
        {
            var actual = TestUtils.DumpH5File(filePath);

            var expected = File
                .ReadAllText($"DumpFiles/data___f__AnonymousType0`4.dump")
                .Replace("<file-path>", filePath)
                .Replace("<type>", "DATASET");

            Assert.Equal(expected, actual);
        }
        finally
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CanWrite_Opaque(bool asMemory)
    {
        // Arrange
        var file = new H5File();

        var data = new byte[] { 0x01, 0x02, 0x03, 0x04 };

        var opaqueInfo = new H5OpaqueInfo(
            TypeSize: 2,
            Tag: "My tag"
        );

        var dataset = asMemory
            ? new H5Dataset(data.AsMemory(), opaqueInfo: opaqueInfo)
            : new H5Dataset(data, opaqueInfo: opaqueInfo);

        file["opaque"] = dataset;

        var filePath = Path.GetTempFileName();

        // Act
        file.Write(filePath);

        // Assert
        try
        {
            var actual = TestUtils.DumpH5File(filePath);

            var expected = File
                .ReadAllText($"DumpFiles/data_opaque.dump")
                .Replace("<file-path>", filePath)
                .Replace("<type>", "DATASET");

            Assert.Equal(expected, actual);
        }
        finally
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
        }
    }

    [Fact]
    public void CanWrite_OpaqueWithDifferentSizes()
    {
        // Arrange
        var data1 = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x10 };
        var data2 = new byte[] { 0x01, 0x02, 0x03, 0x04 };

        var opaqueInfo1 = new H5OpaqueInfo(
            TypeSize: (uint)data1.Length,
            Tag: "My tag"
        );

        var opaqueInfo2 = new H5OpaqueInfo(
            TypeSize: (uint)data2.Length,
            Tag: "My tag"
        );

        var file1 = new H5File();
        file1["opaque1"] = new H5Dataset(data1, opaqueInfo: opaqueInfo1);
        file1["opaque2"] = new H5Dataset(data2, opaqueInfo: opaqueInfo2);

        var filePath1 = Path.GetTempFileName();

        /* Other order */
        var file2 = new H5File();
        file2["opaque1"] = new H5Dataset(data2, opaqueInfo: opaqueInfo2);
        file2["opaque2"] = new H5Dataset(data1, opaqueInfo: opaqueInfo1);

        var filePath2 = Path.GetTempFileName();

        // Act
        file1.Write(filePath1);
        file2.Write(filePath2);

        // Assert 1
        try
        {
            var actual1 = TestUtils.DumpH5File(filePath1);

            var expected1 = File
                .ReadAllText($"DumpFiles/data_opaque_multiple_1.dump")
                .Replace("<file-path>", filePath1)
                .Replace("<type>", "DATASET");

            Assert.Equal(expected1, actual1);
        }
        finally
        {
            if (File.Exists(filePath1))
                File.Delete(filePath1);
        }

        // Assert 2
        try
        {
            var actual2 = TestUtils.DumpH5File(filePath2);

            var expected2 = File
                .ReadAllText($"DumpFiles/data_opaque_multiple_2.dump")
                .Replace("<file-path>", filePath2)
                .Replace("<type>", "DATASET");

            Assert.Equal(expected2, actual2);
        }
        finally
        {
            if (File.Exists(filePath2))
                File.Delete(filePath2);
        }
    }

    [Fact]
    public void CanWrite_ObjectReference()
    {
        // Arrange
        var dataset = new H5Dataset(data: 1);
        var group = new H5Group();

        var file = new H5File
        {
            ["references"] = new H5ObjectReference[] { dataset, dataset, group },
            ["data"] = dataset,
            ["group"] = group
        };

        var filePath = Path.GetTempFileName();

        // Act
        file.Write(filePath);

        // Assert
        try
        {
            var actual = TestUtils.DumpH5File(filePath);

            var expected = File
                .ReadAllText($"DumpFiles/data_object_reference_dataset.dump")
                .Replace("<file-path>", filePath);

            Assert.Equal(expected, actual);
        }
        finally
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
        }
    }

    [Fact]
    public void ThrowsForCircularObjectReference()
    {
        // Arrange
        var data1 = new H5ObjectReference[1];
        var data2 = new H5ObjectReference[1];

        var dataset1 = new H5Dataset(data1);
        var dataset2 = new H5Dataset(data2);

        data1[0] = dataset2;
        data2[0] = dataset1;

        var file = new H5File
        {
            ["data1"] = dataset1,
            ["data2"] = dataset2
        };

        var filePath = Path.GetTempFileName();

        // Act
        void action() => file.Write(filePath);

        // Assert
        var exception = Assert.Throws<TargetInvocationException>(action);

        Assert.Equal(
            "The current object is already being encoded which suggests a circular reference.", 
            exception.InnerException!.InnerException!.Message);
    }

#if NET6_0_OR_GREATER
    [Fact]
    public void CanWrite_MultiDimensionalArray_value_type()
    {
        // Arrange
        var file = new H5File();

        var data = new int[,,]
        {
            {
                {  0,  1,  2 },
                {  3,  4,  5 },
                {  6,  7,  8 }
            },
            {
                {  9, 10, 11 },
                { 12, 13, 14 },
                { 15, 16, 17 }
            },
            {
                { 18, 19, 20 },
                { 21, 22, 23 },
                { 24, 25, 26 }
            },
        };

        var type = data.GetType();

        file[type.Name] = data;

        var filePath = Path.GetTempFileName();

        // Act
        try
        {
            file.Write(filePath);

            // Assert
            var actual = TestUtils.DumpH5File(filePath);

            var expected = File
                .ReadAllText($"DumpFiles/data_{type.Name}.dump")
                .Replace("<file-path>", filePath)
                .Replace("<type>", "DATASET");

            Assert.Equal(expected, actual);
        }
        finally
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
        }
    }

# endif

    [Fact]
    public void CanWrite_MultiDimensionalArray_reference_type()
    {
        // Arrange
        var file = new H5File();

        var data = new string[,,]
        {
            {
                { "A", "B", "C" },
                { "D", "E", "F" },
                { "G", "H", "I" }
            },
            {
                { "J", "K", "L" },
                { "M", "N", "O" },
                { "P", "Q", "R" }
            },
            {
                { "S", "T", "U" },
                { "V", "W", "X" },
                { "Y", "Z", "Ä" }
            },
        };

        var type = data.GetType();

        file[type.Name] = data;

        var filePath = Path.GetTempFileName();

        // Act
        file.Write(filePath);

        // Assert
        try
        {
            var actual = TestUtils.DumpH5File(filePath);

            var expected = File
                .ReadAllText($"DumpFiles/data_{type.Name}.dump")
                .Replace("<file-path>", filePath)
                .Replace("<type>", "DATASET");

            Assert.Equal(expected, actual);
        }
        finally
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
        }
    }

    [Fact]
    public void CanWrite_Large_Array()
    {
        // Arrange
        var file = new H5File();

        foreach (var data in WritingTestData.Numerical)
        {
            var type = data.GetType();
            file[type.Name] = data;
        }

        var filePath = Path.GetTempFileName();

        // Act
        file.Write(filePath);

        // Assert
        try
        {
            var actual = TestUtils.DumpH5File(filePath);

            var expected = File
                .ReadAllText("DumpFiles/data_large_array.dump")
                .Replace("<file-path>", filePath)
                .Replace("<type>", "DATASET");

            Assert.Equal(expected, actual);
        }
        finally
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
        }
    }

#if NET8_0_OR_GREATER
    [Fact]
    public void CanWrite_Large_Array_Int128()
    {
        // Arrange
        var file = new H5File();

        foreach (var data in WritingTestData.Numerical_Int128)
        {
            var type = data.GetType();
            file[type.Name] = data;
        }

        var filePath = Path.GetTempFileName();

        // Act
        file.Write(filePath);

        // Assert
        try
        {
            var actual = TestUtils.DumpH5File(filePath);

            var expected = File
                .ReadAllText("DumpFiles/data_large_array_int128.dump")
                .Replace("<file-path>", filePath)
                .Replace("<type>", "DATASET");

            Assert.Equal(expected, actual);
        }
        finally
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
        }
    }
#endif

    [Fact]
    public void ThrowsForInvalidNumberOfCompoundMembers()
    {
        // Arrange
        var data = new object[] { new Dictionary<string, object>() {
            ["A"] = 1, ["B"] = "-2", ["C"] = 3
        }};

        var type = data.GetType();

        var file = new H5File
        {
            [type.Name] = data
        };

        var filePath = Path.GetTempFileName();

        // Act
        void action() => file.Write(filePath);

        // Assert
        try
        {
            Assert.Throws<TargetInvocationException>(action);
        }
        finally
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
        }
    }
}