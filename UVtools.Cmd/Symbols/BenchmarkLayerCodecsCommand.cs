/*
 *                     GNU AFFERO GENERAL PUBLIC LICENSE
 *                       Version 3, 19 November 2007
 *  Copyright (C) 2007 Free Software Foundation, Inc. <https://fsf.org/>
 *  Everyone is permitted to copy and distribute verbatim copies
 *  of this license document, but changing it is not allowed.
 */

using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Diagnostics;
using UVtools.Core;
using UVtools.Core.Extensions;
using UVtools.Core.Layers;
using ZLinq;

namespace UVtools.Cmd.Symbols;

internal static class BenchmarkLayerCodecsCommand
{
    private sealed record BenchmarkResult
    {
        public LayerCompressionCodec Codec { get; init; }
        public LayerCompressionLevel Level { get; init; }
        public long UncompressedSize { get; init; }
        public long LayersCacheSize { get; init; }
        public string LayersCacheSizeString { get; init; } = string.Empty;
        public TimeSpan Elapsed { get; init; }
        public TimeSpan ElapsedDifference { get; init; }

        /// <summary>
        /// Compression ratio: uncompressed size / compressed size (higher is better)
        /// </summary>
        public double CompressionRatio => UncompressedSize > 0 && LayersCacheSize > 0 
            ? (double)UncompressedSize / LayersCacheSize 
            : 0;
        
        /// <summary>
        /// Compression percentage saved: (1 - compressed/uncompressed) * 100
        /// </summary>
        public double CompressionPercent => UncompressedSize > 0 && LayersCacheSize > 0
            ? (1.0 - (double)LayersCacheSize / UncompressedSize) * 100.0
            : 0;
        
        /// <summary>
        /// Compression throughput in MB/s
        /// </summary>
        public double CompressionMBps => Elapsed.TotalSeconds > 0
            ? UncompressedSize / 1024.0 / 1024.0 / Elapsed.TotalSeconds
            : 0;
        
        /// <summary>
        /// Efficiency score: balances compression ratio and speed
        /// Formula: (CompressionRatio * 100) / (Elapsed.TotalMilliseconds + 1)
        /// Higher is better - rewards good compression and fast speed
        /// </summary>
        public double EfficiencyScore => Elapsed.TotalMilliseconds > 0
            ? (CompressionRatio * 100.0) / (Elapsed.TotalMilliseconds + 1.0)
            : 0;
        
        /// <summary>
        /// Weighted efficiency score: prioritizes compression ratio more than speed
        /// Formula: (CompressionRatio^1.5 * 100) / (Elapsed.TotalMilliseconds + 1)
        /// </summary>
        public double WeightedEfficiencyScore => Elapsed.TotalMilliseconds > 0
            ? (Math.Pow(CompressionRatio, 1.5) * 100.0) / (Elapsed.TotalMilliseconds + 1.0)
            : 0;

        public override string ToString()
        {
            return $"{Codec} @ {Level} | Size: {LayersCacheSizeString} | " +
                   $"Ratio: {CompressionRatio:F2}x | Saved: {CompressionPercent:F2}% | " +
                   $"Time: {Elapsed} | Speed: {CompressionMBps:F2} MB/s | " +
                   $"Efficiency: {EfficiencyScore:F2}";
        }
    }

    internal static Command CreateCommand()
    {
        var command = new Command("benchmark-layer-codecs", "Benchmarks all available layer codecs and return the metrics.")
        {
            GlobalArguments.InputFileArgument,
        };
        
        command.SetAction(result =>
        {
            var inputFile = result.GetRequiredValue(GlobalArguments.InputFileArgument);

            var results = new List<BenchmarkResult>();
            var totalWatch = Stopwatch.StartNew();
            var watch = Stopwatch.StartNew();

            bool first = true;
            long uncompressedSize = 0;

            foreach (var codec in Enum.GetValues<LayerCompressionCodec>())
            {
                CoreSettings.DefaultLayerCompressionCodec = codec;
                Console.WriteLine($"*** Compressing with {codec} ***");
                foreach (var level in Enum.GetValues<LayerCompressionLevel>())
                {
                    // Warning: Skip Brotli highest level due to extreme slowness!!
                    if (codec == LayerCompressionCodec.Brotli && level == LayerCompressionLevel.Highest) continue;
                    CoreSettings.DefaultLayerCompressionLevel = level;
                    
                    watch.Restart();
                    using var slicerFile = Program.OpenInputFile(inputFile);
                    watch.Stop();

                    if (first)
                    {
                        uncompressedSize = (long)slicerFile.DisplayPixelCount * slicerFile.LayerCount;
                    }

                    var benchmarkResult = new BenchmarkResult
                    {
                        Codec = codec,
                        Level = level,
                        UncompressedSize = uncompressedSize,
                        LayersCacheSize = slicerFile.LayersCacheSize,
                        LayersCacheSizeString = slicerFile.LayersCacheSizeString,
                        Elapsed = watch.Elapsed,
                        ElapsedDifference = level != LayerCompressionLevel.Lowest
                            ? watch.Elapsed - results[^1].Elapsed
                            : TimeSpan.Zero,
                    };
                    results.Add(benchmarkResult);

                    if (first)
                    {
                        Console.WriteLine($"""
                                           
                                           *** {slicerFile.Filename} ****
                                           Layer count: {slicerFile.LayerCount}
                                           Resolution: {slicerFile.ResolutionX} x {slicerFile.ResolutionY}
                                           Pixels: {slicerFile.DisplayPixelCount}
                                           Uncompressed size/layer: {SizeExtensions.SizeSuffix(slicerFile.DisplayPixelCount)}
                                           Total uncompressed size: {SizeExtensions.SizeSuffix(uncompressedSize)}
                                           
                                           """);
                        first = false;
                    }

                    Console.WriteLine($"""
                                       - Compressed with {codec} @ {level} level: 
                                         - Layers cache size: {slicerFile.LayersCacheSizeString}
                                         - Compression ratio: {benchmarkResult.CompressionRatio:F2}x ({results[^1].CompressionPercent:F2}% saved)
                                         - Took: {watch.Elapsed} ({benchmarkResult.CompressionMBps:F2} MB/s)
                                         - Efficiency score: {benchmarkResult.EfficiencyScore:F2}
                                         
                                       """);
                }
            }

            totalWatch.Stop();

            Console.WriteLine($"*** Total benchmark time: {totalWatch.Elapsed} ***");

            Console.WriteLine("");
            Console.WriteLine("*** Results: ***");
            foreach (var resultCompression in results)
            {
                Console.WriteLine(resultCompression);
            }

            Console.WriteLine("");
            Console.WriteLine("*** Sorted by best compression ratio: ***");
            foreach (var resultCompression in results.AsValueEnumerable()
                         .OrderByDescending(benchmarkResult => benchmarkResult.CompressionRatio))
            {
                Console.WriteLine(resultCompression);
            }

            Console.WriteLine("");
            Console.WriteLine("*** Sorted by fastest time: ***");
            var fastestResults = results.AsValueEnumerable()
                .OrderBy(benchmarkResult => benchmarkResult.Elapsed);
            foreach (var resultCompression in fastestResults)
            {
                Console.WriteLine(resultCompression);
            }

            Console.WriteLine("");
            Console.WriteLine("*** Sorted by best efficiency score (balanced time/compression): ***");
            var bestEfficiency = results.AsValueEnumerable()
                .OrderByDescending(benchmarkResult => benchmarkResult.EfficiencyScore);
            
            foreach (var resultCompression in bestEfficiency)
            {
                Console.WriteLine(resultCompression);
            }

            Console.WriteLine("");
            Console.WriteLine("*** Sorted by weighted efficiency (favors compression): ***");
            var bestWeightedEfficiency = results.AsValueEnumerable()
                .OrderByDescending(benchmarkResult => benchmarkResult.WeightedEfficiencyScore);
            
            foreach (var resultCompression in bestWeightedEfficiency)
            {
                Console.WriteLine(resultCompression);
            }

            // Display the optimal choices
            var bestOverall = bestEfficiency.First();
            var bestSpeed = fastestResults.First();
            var bestWeighted = bestWeightedEfficiency.First();
            
            Console.WriteLine("");
            Console.WriteLine("*** RECOMMENDED CHOICES: ***");
            Console.WriteLine($"Best balanced (speed + compression): {bestOverall.Codec} @ {bestOverall.Level}");
            Console.WriteLine($"  - Compression: {bestOverall.LayersCacheSizeString}, {bestWeighted.LayersCacheSizeString} {bestOverall.CompressionRatio:F2}x ({bestOverall.CompressionPercent:F2}% saved)");
            Console.WriteLine($"  - Speed: {bestOverall.CompressionMBps:F2} MB/s");
            Console.WriteLine($"  - Efficiency: {bestOverall.EfficiencyScore:F2}");

            Console.WriteLine("");
            Console.WriteLine($"Best for speed: {bestSpeed.Codec} @ {bestSpeed.Level} (Use with high RAM)");
            Console.WriteLine($"  - Compression: {bestSpeed.LayersCacheSizeString}, {bestSpeed.CompressionRatio:F2}x ({bestSpeed.CompressionPercent:F2}% saved)");
            Console.WriteLine($"  - Speed: {bestSpeed.CompressionMBps:F2} MB/s");
            Console.WriteLine($"  - Weighted efficiency: {bestSpeed.WeightedEfficiencyScore:F2}");

            Console.WriteLine("");
            Console.WriteLine($"Best for maximum compression: {bestWeighted.Codec} @ {bestWeighted.Level}");
            Console.WriteLine($"  - Compression: {bestWeighted.LayersCacheSizeString}, {bestWeighted.CompressionRatio:F2}x ({bestWeighted.CompressionPercent:F2}% saved)");
            Console.WriteLine($"  - Speed: {bestWeighted.CompressionMBps:F2} MB/s");
            Console.WriteLine($"  - Weighted efficiency: {bestWeighted.WeightedEfficiencyScore:F2}");
        });

        return command;
    }
}