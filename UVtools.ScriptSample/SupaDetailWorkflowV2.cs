using Emgu.CV.CvEnum;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UVtools.Core;
using UVtools.Core.Operations;
using UVtools.Core.Scripting;
using static UVtools.Core.Operations.OperationLayerImport;
using static UVtools.Core.Operations.OperationPixelArithmetic;

namespace UVtools.ScriptSample
{
    public class SupaDetailWorkflowV2 : ScriptGlobals
    {
        // base object - assumed to have overall heavy aa or blur
        private const string baseTag = "base"; // 3px blur?

        // surface detail - assumed to have aa or blur, split into two to allow different AA levels
        private const string semisharpDetailTag = "semisharp"; // 1px stroke blur or 2px AA?
        private const string softDetailTag = "soft"; // 4px blur?

        // surface detail - no AA, use for finest details
        private const string sharpDetailTag = "sharp";

        // used to subtract voids - no AA use to sharpen cuts into surfaces with AA
        private const string voidsTag = "voids";

        // overall supports - no AA
        private const string supportsTag = "supports";

        // output tags - with and without supports to allow for independent editing of supports due to long processing time
        private const string supaDetailTag = "supadetail";
        private const string supaDetailSupportedTag = "supadetail_supported";

        ScriptNumericalInput<short> FirstPassNoiseMinimumOffset = new()
        {
            Label = "First pass minimum noise offset",
            ToolTip = "Lower bound of uniform noise to dither surface (this pass applies to skin only)",
            Minimum = -1000,
            Maximum = 1000,
            Increment = 1,
            Value = -70
        };

        ScriptNumericalInput<short> FirstPassNoiseMaximumOffset = new()
        {
            Label = "First pass maximum noise offset",
            ToolTip = "Upper bound of uniform noise to dither surface (this pass applies to skin only)",
            Minimum = -1000,
            Maximum = 1000,
            Increment = 1,
            Value = 20
        };

        ScriptNumericalInput<short> SecondPassNoiseMinimumOffset = new()
        {
            Label = "Second pass minimum noise offset",
            ToolTip = "Lower bound of uniform noise to dither surface (this pass applies to skin and subsurface only)",
            Minimum = -1000,
            Maximum = 1000,
            Increment = 1,
            Value = -80
        };

        ScriptNumericalInput<short> SecondPassNoiseMaximumOffset = new()
        {
            Label = "Second pass maximum noise offset",
            ToolTip = "Upper bound of uniform noise to dither surface (this pass applies to skin and subsurface only)",
            Minimum = -1000,
            Maximum = 1000,
            Increment = 1,
            Value = 20
        };

        ScriptNumericalInput<short> ThirdPassNoiseMinimumOffset = new()
        {
            Label = "Third pass minimum noise offset",
            ToolTip = "Lower bound of uniform noise to dither surface (this pass applies to skin, subsurface and corrosion depths)",
            Minimum = -1000,
            Maximum = 1000,
            Increment = 1,
            Value = -55
        };

        ScriptNumericalInput<short> ThirdPassNoiseMaximumOffset = new()
        {
            Label = "Third pass maximum noise offset",
            ToolTip = "Upper bound of uniform noise to dither surface (this pass applies to skin, subsurface and corrosion depths)",
            Minimum = -1000,
            Maximum = 1000,
            Increment = 1,
            Value = 20
        };

        ScriptNumericalInput<byte> BaseFeatureDepth = new()
        {
            Label = "Base feature depth", //todo: replace with quantisation perhaps
            ToolTip = "Number of iterations to seek features ~= pixel thickness",
            Minimum = 0,
            Maximum = 100,
            Increment = 1,
            Value = 6
        };

        ScriptNumericalInput<byte> SoftFeatureDepth = new()
        {
            Label = "Soft feature depth", //todo: replace with quantisation perhaps
            ToolTip = "Number of iterations to seek features ~= pixel thickness",
            Minimum = 0,
            Maximum = 100,
            Increment = 1,
            Value = 2
        };

        ScriptNumericalInput<byte> SemisharpFeatureDepth = new()
        {
            Label = "Semisharp feature depth", //todo: replace with quantisation perhaps
            ToolTip = "Number of iterations to seek features ~= pixel thickness",
            Minimum = 0,
            Maximum = 100,
            Increment = 1,
            Value = 2
        };

        ScriptNumericalInput<byte> PixelsOfAAToRemove = new()
        {
            Label = "Pixels of AA to remove", //todo: replace with quantisation perhaps
            ToolTip = "Enhance contrast by removing this many pixels of AA (including corrosion) from touching elements",
            Minimum = 1,
            Maximum = 4,
            Increment = 1,
            Value = 3
        };

        ScriptNumericalInput<byte> SkinDepth = new()
        {
            Label = "Pixels depth of skin",
            ToolTip = "Depth of skin added onto the original model - keep depth low, corrode this heavily",
            Minimum = 1,
            Maximum = 4,
            Increment = 1,
            Value = 1
        };

        ScriptNumericalInput<byte> SubsurfaceDepth = new()
        {
            Label = "Pixels depth of subsurface", 
            ToolTip = "Depth of noise added inside original model - keep depth low, corrode this moderately to strongly",
            Minimum = 1,
            Maximum = 4,
            Increment = 1,
            Value = 1
        };

        ScriptNumericalInput<byte> CorrosionDepth = new()
        {
            Label = "Pixels depth of corrosion",
            ToolTip = "Depth of weaker noise added inside original model - keep depth moderate, corrode this mildly to moderately",
            Minimum = 1,
            Maximum = 4,
            Increment = 1,
            Value = 2
        };

        private ScriptCheckBoxInput ApplyThreshold = new()
        {
            Label = "Apply threshold",
            Value = false
        };

        ScriptNumericalInput<byte> ToZeroThreshold = new()
        {
            Label = "Zero threshold", //todo: replace with quantisation perhaps
            ToolTip = "Zero threshold to apply to final slices",
            Minimum = byte.MinValue,
            Maximum = byte.MaxValue,
            Increment = 1,
            Value = 119
        };

        ScriptNumericalInput<byte> BaseBrightnessFalloff = new()
        {
            Label = "Base brightness falloff",
            ToolTip = "Base brightness falloff",
            Minimum = byte.MinValue,
            Maximum = byte.MaxValue,
            Increment = 1,
            Value = 30
        };

        ScriptNumericalInput<byte> SoftDetailBrightnessFalloff = new()
        {
            Label = "Soft detail brightness falloff",
            ToolTip = "Soft detail brightness falloff",
            Minimum = byte.MinValue,
            Maximum = byte.MaxValue,
            Increment = 1,
            Value = 30
        };

        ScriptNumericalInput<byte> SemisharpDetailBrightnessFalloff = new()
        {
            Label = "Semisharp detail brightness falloff",
            ToolTip = "Semisharp detail brightness falloff",
            Minimum = byte.MinValue,
            Maximum = byte.MaxValue,
            Increment = 1,
            Value = 20
        };

        ScriptNumericalInput<byte> SkinBrightness = new()
        {
            Label = "Skin brightnesss", //todo: replace with quantisation perhaps
            ToolTip = "Initial brightness to set the skin to - allows added surface to be taregtted more aggressively than pixels that were part of the model originally",
            Minimum = byte.MinValue,
            Maximum = byte.MaxValue,
            Increment = 1,
            Value = 220
        };

        private ScriptCheckBoxInput RemoveEmptyLayersAtEnd = new()
        {
            Label = "Remove empty layers at end",
            Value = true
        };

        public void ScriptInit()
        {
            Script.Name = "Automate SupaDetail! workflow";
            Script.Description = "A workflow automation to combine selective AA, countersunk voids, surface corrosion, etc";
            Script.Author = "Finn Neuik";
            Script.Version = new Version(2, 0);
            Script.UserInputs.AddRange(new ScriptBaseInput[] {
                FirstPassNoiseMinimumOffset,
                FirstPassNoiseMaximumOffset,
                SecondPassNoiseMinimumOffset,
                SecondPassNoiseMaximumOffset,
                ThirdPassNoiseMinimumOffset,
                ThirdPassNoiseMaximumOffset,
                BaseFeatureDepth,
                SoftFeatureDepth,
                SemisharpFeatureDepth,
                PixelsOfAAToRemove,
                SkinDepth,
                SubsurfaceDepth,
                CorrosionDepth,
                SkinBrightness,
                ApplyThreshold,
                ToZeroThreshold,
                BaseBrightnessFalloff,
                SoftDetailBrightnessFalloff,
                SemisharpDetailBrightnessFalloff
            });
        }

        private string CompositionFileName(string part)
        {
            var filenameWithoutExtension = Path.GetFileNameWithoutExtension(SlicerFile.FileFullPath);
            var coreFilename = filenameWithoutExtension.Remove(filenameWithoutExtension.LastIndexOf('.'));
            var extension = Path.GetExtension(SlicerFile.FileFullPath);

            return Path.Combine(Path.GetDirectoryName(SlicerFile.FileFullPath), (coreFilename + "." + part + extension));
        }

        public string ScriptValidate()
        {
            var missingFiles = new List<string> { baseTag, semisharpDetailTag, softDetailTag, sharpDetailTag, voidsTag, supportsTag }
                .Select(CompositionFileName)
                .Where(f => !File.Exists(f));

            if (!missingFiles.Any())
            {
                return null;
            }

            return "Missing the following files:" + String.Join(Environment.NewLine, missingFiles);
        }

        public bool ScriptExecute()
        {
            //todo: very inefficient, look to load in matrices and only save when intermediate results are useful

            Progress.Title = "Creating diffused elements";
            var diffusedBase = CreateDiffuse(baseTag, SkinDepth.Value, BaseBrightnessFalloff.Value, SkinBrightness.Value, SubsurfaceDepth.Value, CorrosionDepth.Value, BaseFeatureDepth.Value);
            var diffusedSoft = CreateDiffuse(softDetailTag, SkinDepth.Value, SoftDetailBrightnessFalloff.Value, SkinBrightness.Value, SubsurfaceDepth.Value, CorrosionDepth.Value, SoftFeatureDepth.Value);
            var diffusedSemisharp = CreateDiffuse(semisharpDetailTag, SkinDepth.Value, SemisharpDetailBrightnessFalloff.Value, SkinBrightness.Value, SubsurfaceDepth.Value, 0, SemisharpFeatureDepth.Value);

            Progress.Title = "Creating cutters";
            var dilatedSharpDetailCutter = CreateCutter(sharpDetailTag, PixelsOfAAToRemove.Value);
            var dilatedSoftDetailCutter = CreateCutter(softDetailTag, PixelsOfAAToRemove.Value);
            var dilatedSemisharpDetailCutter = CreateCutter(semisharpDetailTag, PixelsOfAAToRemove.Value);
            var dilatedBaseCutter = CreateCutter(baseTag, PixelsOfAAToRemove.Value);
            var dilatedSupportsCutter = CreateCutter(supportsTag, 2);

            // create tooling - use cutters to remove material from surrounding models that have AA, significantly enhancing sharpness
            Progress.Title = "Creating contrasted elements";
            var contrastedSemisharpDetailAA = CreateContrasted(semisharpDetailTag, diffusedSemisharp, dilatedBaseCutter, dilatedSoftDetailCutter, dilatedSharpDetailCutter, dilatedSupportsCutter);
            var contrastedSoftDetailAA = CreateContrasted(softDetailTag, diffusedSoft, dilatedBaseCutter, dilatedSemisharpDetailCutter, dilatedSharpDetailCutter, dilatedSupportsCutter);
            var contrastedBaseAA = CreateContrasted(baseTag, diffusedBase, dilatedSoftDetailCutter, dilatedSemisharpDetailCutter, dilatedSharpDetailCutter, dilatedSupportsCutter);

            //compose model elements
            Progress.Title = "Composing model elements";
            List<Operation> operations = new();

            OperationLayerImport importBaseElement = new(SlicerFile)
            {
                ImportType = ImportTypes.Replace,
            };
            importBaseElement.AddFile(CompositionFileName(contrastedBaseAA));
            operations.Add(importBaseElement);

            OperationLayerImport restoreCores = new(SlicerFile)
            {
                ImportType = ImportTypes.MergeMax
            };
            restoreCores.AddFile(CompositionFileName(contrastedSoftDetailAA));
            restoreCores.AddFile(CompositionFileName(contrastedSemisharpDetailAA));
            operations.Add(restoreCores);

            //reinstate voids
            OperationLayerImport subtractVoidElement = new(SlicerFile)
            {
                ImportType = ImportTypes.Subtract
            };
            subtractVoidElement.AddFile(CompositionFileName(voidsTag));
            operations.Add(subtractVoidElement);

            OperationLayerImport restoreRaw = new(SlicerFile)
            {
                ImportType = ImportTypes.MergeMax
            };
            restoreCores.AddFile(CompositionFileName(sharpDetailTag));
            operations.Add(restoreRaw);

            RoundupOperations(operations);

            SlicerFile.SaveAs(CompositionFileName(supaDetailTag), Progress);

            OperationLayerImport restoreSupports = new(SlicerFile)
            {
                ImportType = ImportTypes.MergeMax
            };
            restoreSupports.AddFile(CompositionFileName(supportsTag));
            operations.Add(restoreSupports);

            // apply thresholding
            if (ApplyThreshold.Value)
            {
                OperationPixelArithmetic threshold = new(SlicerFile)
                {
                    Operator = PixelArithmeticOperators.Threshold,
                    ApplyMethod = PixelArithmeticApplyMethod.Model,
                    UsePattern = false,
                    Value = ToZeroThreshold.Value,
                    ThresholdMaxValue = 255,
                    ThresholdType = ThresholdType.ToZero
                };
                operations.Add(threshold);
            }

            if (RemoveEmptyLayersAtEnd.Value)
            {
                OperationRepairLayers removeEmpty = new(SlicerFile)
                {
                    RemoveEmptyLayers = true,
                    RepairIslands = false,
                    RepairResinTraps = false,
                    RepairSuctionCups = false
                };
                operations.Add(removeEmpty);
            }
            
            RoundupOperations(operations);

            //var supportedOutputTag = String.Join('_', supaDetailSupportedTag, SlicerFile.LayerHeight, /* SlicerFile.ExposureRepresentation, */DateTime.UtcNow.ToString("yyMMdd_HHmm"));
            SlicerFile.SaveAs(CompositionFileName(supaDetailSupportedTag + "_" + DateTime.UtcNow.ToString("yyMMddHHmm")), Progress);

            return !Progress.Token.IsCancellationRequested;

        }

        private string CreateDiffuse(string elementTag, uint modelSkinDepth, byte brightnessFalloff, byte skinBrightness, uint subsurfaceDepth, uint corrosionDepth, uint featureDepth) {
            List<Operation> operations = new();

            // prepare base element
            Progress.Title = "Creating diffuse " + elementTag;
            OperationLayerImport importBaseElement = new(SlicerFile)
            {
                ImportType = ImportTypes.Replace
            };
            importBaseElement.AddFile(CompositionFileName(elementTag));
            operations.Add(importBaseElement);

            OperationMorph isolateFeatures = new(SlicerFile)
            {
                MorphOperation = OperationMorph.MorphOperations.IsolateFeatures,
                Iterations = featureDepth,
                LayerIndexStart = 0,
                LayerIndexEnd = SlicerFile.LastLayerIndex,
                LayerRangeSelection = Enumerations.LayerRangeSelection.All
            };
            operations.Add(isolateFeatures);

            RoundupOperations(operations);

            SlicerFile.SaveAs(CompositionFileName(elementTag + "_features"), Progress);

            operations = new();

            OperationLayerImport importBaseElement2 = new(SlicerFile)
            {
                ImportType = ImportTypes.Replace
            };
            importBaseElement.AddFile(CompositionFileName(elementTag));
            operations.Add(importBaseElement2);

            RoundupOperations(operations);

            // remove skin from base model so when used for restoration corrosion bites into surface
            OperationMorph dilateBaseElement = new(SlicerFile)
            {
                MorphOperation = OperationMorph.MorphOperations.Dilate,
                Iterations = modelSkinDepth,
                LayerIndexStart = 0,
                LayerIndexEnd = SlicerFile.LastLayerIndex,
                LayerRangeSelection = Enumerations.LayerRangeSelection.All
            };
            operations.Add(dilateBaseElement);

            OperationPixelArithmetic setSkinBrightness = new(SlicerFile)
            {
                Operator = PixelArithmeticOperators.Set,
                ApplyMethod = PixelArithmeticApplyMethod.Model,
                UsePattern = false,
                Value = skinBrightness,
            };
            operations.Add(setSkinBrightness);

            Progress.Title = "Reinstating " + elementTag;
            OperationLayerImport restoreBaseElement = new(SlicerFile)
            {
                ImportType = ImportTypes.MergeMax
            };
            restoreBaseElement.AddFile(CompositionFileName(elementTag));
            operations.Add(restoreBaseElement);

            if (subsurfaceDepth > 0)
            {
                OperationPixelArithmetic setModelWallBrightness = new(SlicerFile)
                {
                    Operator = PixelArithmeticOperators.Subtract,
                    ApplyMethod = PixelArithmeticApplyMethod.ModelWalls,
                    UsePattern = false,
                    Value = brightnessFalloff,
                    WallThickness = modelSkinDepth + subsurfaceDepth
                };
                operations.Add(setModelWallBrightness);
            }

            if (corrosionDepth > 0)
            {
                OperationPixelArithmetic setModelWallBrightness2 = new(SlicerFile)
                {
                    Operator = PixelArithmeticOperators.Subtract,
                    ApplyMethod = PixelArithmeticApplyMethod.ModelWalls,
                    UsePattern = false,
                    Value = brightnessFalloff,
                    WallThickness = modelSkinDepth + subsurfaceDepth + corrosionDepth
                };
                operations.Add(setModelWallBrightness2);
            }

            OperationPixelArithmetic corrode = new(SlicerFile)
            {
                Operator = PixelArithmeticOperators.Corrode,
                ApplyMethod = PixelArithmeticApplyMethod.ModelWalls,
                WallThickness = modelSkinDepth,
                NoiseThreshold = 0,
                NoiseMinOffset = FirstPassNoiseMinimumOffset.Value,
                NoiseMaxOffset = FirstPassNoiseMaximumOffset.Value
            };
            operations.Add(corrode);

            if (subsurfaceDepth > 0)
            {
                OperationPixelArithmetic corrode2 = new(SlicerFile)
                {
                    Operator = PixelArithmeticOperators.Corrode,
                    ApplyMethod = PixelArithmeticApplyMethod.ModelWalls,
                    WallThickness = modelSkinDepth + subsurfaceDepth,
                    NoiseThreshold = 0,
                    NoiseMinOffset = SecondPassNoiseMinimumOffset.Value,
                    NoiseMaxOffset = SecondPassNoiseMaximumOffset.Value
                };
                operations.Add(corrode2);
            }

            if (corrosionDepth > 0)
            {
                OperationPixelArithmetic corrode3 = new(SlicerFile)
                {
                    Operator = PixelArithmeticOperators.Corrode,
                    ApplyMethod = PixelArithmeticApplyMethod.ModelWalls,
                    WallThickness = modelSkinDepth + subsurfaceDepth + corrosionDepth,
                    NoiseThreshold = 0,
                    NoiseMinOffset = ThirdPassNoiseMinimumOffset.Value,
                    NoiseMaxOffset = ThirdPassNoiseMaximumOffset.Value
                };
                operations.Add(corrode3);
            }

            OperationLayerImport restoreElementDetailFeatures = new(SlicerFile)
            {
                ImportType = ImportTypes.MergeMax
            };
            restoreElementDetailFeatures.AddFile(CompositionFileName(elementTag + "_features"));
            operations.Add(restoreElementDetailFeatures);

            Progress.Title = "Diffusing " + elementTag;
            RoundupOperations(operations);

            var outputTag = elementTag + "_diffused";
            SlicerFile.SaveAs(CompositionFileName(outputTag), Progress);

            return outputTag;
        }

        private string CreateContrasted(string elementTag, string diffusedElementTag, params string[] cutters)
        {
            // diffuse, less all cutters
            List<Operation> operations = new();
            OperationLayerImport importDiffuseElement = new(SlicerFile)
            {
                ImportType = ImportTypes.Replace
            };
            importDiffuseElement.AddFile(CompositionFileName(diffusedElementTag));
            operations.Add(importDiffuseElement);

            OperationLayerImport subtractElements = new(SlicerFile)
            {
                ImportType = ImportTypes.Subtract
            };
            foreach (var cutter in cutters)
            {
                subtractElements.AddFile(CompositionFileName(cutter));
            }
            operations.Add(subtractElements);

            RoundupOperations(operations);

            var intermediateTag = diffusedElementTag + "_contrasted_intermediate";

            SlicerFile.SaveAs(CompositionFileName(intermediateTag), Progress);

            // cutters and original (to be restored)
            operations = new();

            OperationPixelArithmetic clearSlices = new(SlicerFile)
            {
                Operator = PixelArithmeticOperators.Set,
                Value = 0,
                ApplyMethod = PixelArithmeticApplyMethod.All,
            };
            operations.Add(clearSlices);

            OperationLayerImport importBaseElement = new(SlicerFile)
            {
                ImportType = ImportTypes.MergeMax
            };
            foreach (var cutter in cutters)
            {
                importBaseElement.AddFile(CompositionFileName(cutter));
            }
            operations.Add(importBaseElement);

            OperationLayerImport importElement = new(SlicerFile)
            {
                ImportType = ImportTypes.BitwiseAnd
            };
            importElement.AddFile(CompositionFileName(elementTag));
            operations.Add(importElement);

            OperationLayerImport restoreElements = new(SlicerFile)
            {
                ImportType = ImportTypes.MergeMax
            };
            restoreElements.AddFile(CompositionFileName(intermediateTag));
            operations.Add(restoreElements);

            RoundupOperations(operations);

            var newTag = diffusedElementTag + "_contrasted";

            SlicerFile.SaveAs(CompositionFileName(newTag), Progress);

            return newTag;
        }

        private void RoundupOperations(List<Operation> operations)
        {
            foreach (var operation in operations)
            {
                Progress.Token.ThrowIfCancellationRequested();
                if (!operation.CanValidate()) continue;
                operation.Execute(Progress);
            }
        }

        private string CreateCutter(string elementTag, uint dilationPixels)
        {
            List<Operation> operations = new();

            OperationLayerImport importFlatDetailElement = new(SlicerFile)
            {
                ImportType = ImportTypes.Replace
            };
            importFlatDetailElement.AddFile(CompositionFileName(elementTag));
            operations.Add(importFlatDetailElement);

            OperationMorph dilateFlatDetail = new(SlicerFile)
            {
                MorphOperation = OperationMorph.MorphOperations.Dilate,
                Iterations = dilationPixels,
                LayerIndexStart = 0,
                LayerIndexEnd = SlicerFile.LastLayerIndex,
                LayerRangeSelection = Enumerations.LayerRangeSelection.All

            };
            operations.Add(dilateFlatDetail);

            RoundupOperations(operations);

            var newTag = elementTag + "_dilated";

            SlicerFile.SaveAs(CompositionFileName(newTag), Progress);

            return newTag;
        }
    }
}
