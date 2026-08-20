// Copyright (c) University of Washington 2026. Licensed under the MIT License.
// Versioned, self-describing model file. Replaces the Python pickle, which could not be
// read outside a matching Python + xgboost install.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using pwiz.Osprey.ML;

namespace MARS.Core;

/// <summary>On-disk representation of a trained MARS model.</summary>
public sealed class MarsModelFile
{
    /// <summary>
    /// Bumped whenever the meaning of a field changes. A reader that does not recognize
    /// the version refuses the file rather than guessing.
    /// </summary>
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; set; } = CurrentFormatVersion;

    public string MarsVersion { get; set; } = MarsInfo.Version;

    /// <summary>Feature names in model order. Loading fails if any name is unknown.</summary>
    public List<string> FeatureNames { get; set; } = new();

    /// <summary>Seconds subtracted from raw acquisition timestamps before training.</summary>
    public double AbsoluteTimeOffset { get; set; }

    public CalibrationOptionsDto Options { get; set; } = new();

    public GbtModelDto Model { get; set; } = new();

    public TrainingSummaryDto? Training { get; set; }

    public sealed class CalibrationOptionsDto
    {
        public int NEstimators { get; set; }

        public int MaxDepth { get; set; }

        public double LearningRate { get; set; }

        public double MinChildWeight { get; set; }

        public double Subsample { get; set; }

        public double ColSampleByTree { get; set; }

        public double Gamma { get; set; }

        public double RegLambda { get; set; }

        public double RegAlpha { get; set; }

        public int MaxBins { get; set; }

        public int Seed { get; set; }

        public double ValidationSplit { get; set; }

        public bool WeightByIntensity { get; set; }
    }

    public sealed class GbtModelDto
    {
        public double BaseScore { get; set; }

        /// <summary>
        /// Feature-vector width the ensemble was trained on. Always equal to the length of
        /// <see cref="MarsModelFile.FeatureNames"/>, and written anyway so the tree arrays
        /// are self-describing to a reader that only looks at this object.
        /// </summary>
        public int FeatureCount { get; set; }

        /// <summary>Loss the ensemble was fitted under. MARS always uses squared error.</summary>
        public GbtObjective Objective { get; set; } = GbtObjective.SquaredError;

        public int[] Feature { get; set; } = Array.Empty<int>();

        public double[] Threshold { get; set; } = Array.Empty<double>();

        public int[] Left { get; set; } = Array.Empty<int>();

        public int[] Right { get; set; } = Array.Empty<int>();

        public double[] Leaf { get; set; } = Array.Empty<double>();

        public int[] TreeRoot { get; set; } = Array.Empty<int>();
    }

    public sealed class TrainingSummaryDto
    {
        public int RowsMatched { get; set; }

        public int RowsUsed { get; set; }

        public int RowsTrain { get; set; }

        public int RowsValidation { get; set; }

        public double TrainMae { get; set; }

        public double TrainRmse { get; set; }

        public double ValidationMae { get; set; }

        public double ValidationRmse { get; set; }

        public double BeforeStdDev { get; set; }

        public double AfterStdDev { get; set; }

        public double BeforeMad { get; set; }

        public double AfterMad { get; set; }

        public double[] PermutationImportance { get; set; } = Array.Empty<double>();

        public int[] SplitCount { get; set; } = Array.Empty<int>();
    }
}

public static class MarsInfo
{
    /// <summary>
    /// MARS version, read from the assembly rather than declared here, so
    /// dotnet/Directory.Build.props is the single place a release bumps. A duplicated
    /// literal is the kind of thing that stays correct until exactly the release where it
    /// does not, and it is stamped into every model file MARS writes.
    /// </summary>
    public static string Version { get; } = ReadAssemblyVersion();

    private static string ReadAssemblyVersion()
    {
        Assembly assembly = typeof(MarsInfo).Assembly;
        string? informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (string.IsNullOrEmpty(informational))
            return assembly.GetName().Version?.ToString(3) ?? "0.0.0";

        // The SDK appends "+<commit sha>" when source link is on; the build metadata is
        // not part of the version a user should see.
        int plus = informational.IndexOf('+');
        return plus < 0 ? informational : informational[..plus];
    }
}

public static class MarsModelIo
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static void Save(MzCalibrator calibrator, string path)
    {
        GbtModelData data = calibrator.Model.ToModelData();
        TrainingStatistics? stats = calibrator.Statistics;

        var file = new MarsModelFile
        {
            FeatureNames = new List<string>(calibrator.Features.Names()),
            AbsoluteTimeOffset = calibrator.AbsoluteTimeOffset,
            Options = new MarsModelFile.CalibrationOptionsDto
            {
                NEstimators = calibrator.Options.NEstimators,
                MaxDepth = calibrator.Options.MaxDepth,
                LearningRate = calibrator.Options.LearningRate,
                MinChildWeight = calibrator.Options.MinChildWeight,
                Subsample = calibrator.Options.Subsample,
                ColSampleByTree = calibrator.Options.ColSampleByTree,
                Gamma = calibrator.Options.Gamma,
                RegLambda = calibrator.Options.RegLambda,
                RegAlpha = calibrator.Options.RegAlpha,
                MaxBins = calibrator.Options.MaxBins,
                Seed = calibrator.Options.Seed,
                ValidationSplit = calibrator.Options.ValidationSplit,
                WeightByIntensity = calibrator.Options.WeightByIntensity,
            },
            Model = new MarsModelFile.GbtModelDto
            {
                BaseScore = data.BaseScore,
                FeatureCount = data.FeatureCount,
                Objective = data.Objective,
                Feature = data.Feature,
                Threshold = data.Threshold,
                Left = data.Left,
                Right = data.Right,
                Leaf = data.Leaf,
                TreeRoot = data.TreeRoot,
            },
            Training = stats is null ? null : new MarsModelFile.TrainingSummaryDto
            {
                RowsMatched = stats.RowsMatched,
                RowsUsed = stats.RowsUsed,
                RowsTrain = stats.RowsTrain,
                RowsValidation = stats.RowsValidation,
                TrainMae = stats.TrainMae,
                TrainRmse = stats.TrainRmse,
                ValidationMae = stats.ValidationMae,
                ValidationRmse = stats.ValidationRmse,
                BeforeStdDev = stats.Before.StdDev,
                AfterStdDev = stats.After.StdDev,
                BeforeMad = stats.Before.Mad,
                AfterMad = stats.After.Mad,
                PermutationImportance = stats.PermutationImportance,
                SplitCount = stats.SplitCount,
            },
        };

        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        using FileStream stream = File.Create(path);
        JsonSerializer.Serialize(stream, file, SerializerOptions);
    }

    public static MzCalibrator Load(string path)
    {
        using FileStream stream = File.OpenRead(path);
        MarsModelFile? file = JsonSerializer.Deserialize<MarsModelFile>(stream, SerializerOptions);
        if (file is null) throw new InvalidDataException($"Model file is empty or malformed: {path}");

        if (file.FormatVersion != MarsModelFile.CurrentFormatVersion)
        {
            throw new InvalidDataException(
                $"Model file '{path}' is format version {file.FormatVersion}; this build reads version {MarsModelFile.CurrentFormatVersion}.");
        }

        // A model whose feature list does not match the extractor's vocabulary is a hard
        // error, not a warning: silently scoring a differently shaped row would corrupt
        // every m/z it touched.
        FeatureSet features = FeatureSet.FromNames(file.FeatureNames);

        var data = new GbtModelData
        {
            BaseScore = file.Model.BaseScore,

            // A file written before these two fields existed carries the same information
            // in its feature name list, and MARS only ever fits squared error, so derive
            // rather than reject.
            FeatureCount = file.Model.FeatureCount > 0 ? file.Model.FeatureCount : features.Count,
            Objective = file.Model.Objective,
            Feature = file.Model.Feature,
            Threshold = file.Model.Threshold,
            Left = file.Model.Left,
            Right = file.Model.Right,
            Leaf = file.Model.Leaf,
            TreeRoot = file.Model.TreeRoot,
        };

        GradientBoostedTrees model = GradientBoostedTrees.FromModelData(data);

        var options = new CalibrationOptions
        {
            NEstimators = file.Options.NEstimators,
            MaxDepth = file.Options.MaxDepth,
            LearningRate = file.Options.LearningRate,
            MinChildWeight = file.Options.MinChildWeight,
            Subsample = file.Options.Subsample,
            ColSampleByTree = file.Options.ColSampleByTree,
            Gamma = file.Options.Gamma,
            RegLambda = file.Options.RegLambda,
            RegAlpha = file.Options.RegAlpha,
            MaxBins = file.Options.MaxBins,
            Seed = file.Options.Seed,
            ValidationSplit = file.Options.ValidationSplit,
            WeightByIntensity = file.Options.WeightByIntensity,
        };

        return new MzCalibrator(features, model, file.AbsoluteTimeOffset, options, null);
    }
}
