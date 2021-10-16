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
    public class SupaDetailWorkflowV3 : ScriptGlobals
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

        List<string> _originalFiles = new List<string> { baseTag, semisharpDetailTag, softDetailTag, sharpDetailTag, voidsTag, supportsTag };

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
            Value = 30
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
            Value = 30
        };

        ScriptNumericalInput<short> ThirdPassNoiseMinimumOffset = new()
        {
            Label = "Third pass minimum noise offset",
            ToolTip = "Lower bound of uniform noise to dither surface (this pass applies to skin, subsurface and corrosion depths)",
            Minimum = -1000,
            Maximum = 1000,
            Increment = 1,
            Value = -35
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
            Value = 12
        };

        ScriptNumericalInput<byte> SoftFeatureDepth = new()
        {
            Label = "Soft feature depth", //todo: replace with quantisation perhaps
            ToolTip = "Number of iterations to seek features ~= pixel thickness",
            Minimum = 0,
            Maximum = 100,
            Increment = 1,
            Value = 6
        };

        ScriptNumericalInput<byte> SemisharpFeatureDepth = new()
        {
            Label = "Semisharp feature depth", //todo: replace with quantisation perhaps
            ToolTip = "Number of iterations to seek features ~= pixel thickness",
            Minimum = 0,
            Maximum = 100,
            Increment = 1,
            Value = 4
        };

        ScriptNumericalInput<byte> PixelsOfAAToRemove = new()
        {
            Label = "Pixels of AA to remove", //todo: replace with quantisation perhaps
            ToolTip = "Enhance contrast by removing this many pixels of AA (including corrosion) from touching elements",
            Minimum = 1,
            Maximum = 4,
            Increment = 1,
            Value = 2
        };

        ScriptNumericalInput<byte> SkinDepth = new()
        {
            Label = "Pixels depth of skin",
            ToolTip = "Depth of skin added onto the original model - keep depth low, corrode this heavily",
            Minimum = 0,
            Maximum = 4,
            Increment = 1,
            Value = 2
        };

        ScriptNumericalInput<byte> SubsurfaceDepth = new()
        {
            Label = "Pixels depth of subsurface", 
            ToolTip = "Depth of noise added inside original model - keep depth low, corrode this moderately to strongly",
            Minimum = 0,
            Maximum = 4,
            Increment = 1,
            Value = 1
        };

        ScriptNumericalInput<byte> CorrosionDepth = new()
        {
            Label = "Pixels depth of corrosion",
            ToolTip = "Depth of weaker noise added inside original model - keep depth moderate, corrode this mildly to moderately",
            Minimum = 0,
            Maximum = 4,
            Increment = 1,
            Value = 1
        };

        private ScriptCheckBoxInput ApplyThreshold = new()
        {
            Label = "Apply threshold",
            Value = false
        };

        private ScriptCheckBoxInput ApplyCorrosion = new()
        {
            Label = "Apply corrosion",
            Value = true
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

        ScriptNumericalInput<byte> BaseModelSkinBrightnessFalloff = new()
        {
            Label = "Skin brightnesss falloff", //todo: replace with quantisation perhaps
            ToolTip = "Initial brightness to set the skin to - allows added surface to be taregtted more aggressively than pixels that were part of the model originally",
            Minimum = byte.MinValue,
            Maximum = byte.MaxValue,
            Increment = 1,
            Value = 45
        };

        ScriptNumericalInput<byte> SoftModelSkinBrightnessFalloff = new()
        {
            Label = "Skin brightnesss falloff", //todo: replace with quantisation perhaps
            ToolTip = "Initial brightness to set the skin to - allows added surface to be taregtted more aggressively than pixels that were part of the model originally",
            Minimum = byte.MinValue,
            Maximum = byte.MaxValue,
            Increment = 1,
            Value = 45
        };

        ScriptNumericalInput<byte> SemisharpModelSkinBrightnessFalloff = new()
        {
            Label = "Skin brightnesss falloff", //todo: replace with quantisation perhaps
            ToolTip = "Initial brightness to set the skin to - allows added surface to be taregtted more aggressively than pixels that were part of the model originally",
            Minimum = byte.MinValue,
            Maximum = byte.MaxValue,
            Increment = 1,
            Value = 60
        };

        private ScriptCheckBoxInput RemoveEmptyLayersAtEnd = new()
        {
            Label = "Remove empty layers at end",
            Value = false
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
                BaseModelSkinBrightnessFalloff,
                SoftModelSkinBrightnessFalloff,
                SemisharpModelSkinBrightnessFalloff,
                ApplyThreshold,
                ToZeroThreshold,
                BaseBrightnessFalloff,
                SoftDetailBrightnessFalloff,
                SemisharpDetailBrightnessFalloff,
                ApplyCorrosion,
                RemoveEmptyLayersAtEnd
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
            var missingFiles = _originalFiles
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

            // load mats
            //Script.UserInputs.Select(ui => ui.Label +":" + ((dynamic)ui).Value);

            //var originaLayers = _originalFiles.ToDictionary(f => f, f=> {
            //    var ff = FileFormat.FindByExtensionOrFilePath(CompositionFileName(f));
            //    ff.Decode(CompositionFileName(f));
            //    return ff.Select(l => l.LayerMat);
            //});

            Progress.Title = "Creating diffused elements";
            var diffusedBase = CreateDiffuse(baseTag, SkinDepth.Value, BaseBrightnessFalloff.Value, BaseModelSkinBrightnessFalloff.Value, SubsurfaceDepth.Value, CorrosionDepth.Value, BaseFeatureDepth.Value);
            var diffusedSoft = CreateDiffuse(softDetailTag, SkinDepth.Value, SoftDetailBrightnessFalloff.Value, SoftModelSkinBrightnessFalloff.Value, SubsurfaceDepth.Value, CorrosionDepth.Value, SoftFeatureDepth.Value);
            var diffusedSemisharp = CreateDiffuse(semisharpDetailTag, 1, SemisharpDetailBrightnessFalloff.Value, SemisharpModelSkinBrightnessFalloff.Value, SubsurfaceDepth.Value, 0, SemisharpFeatureDepth.Value);

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

            ReplaceActiveFile(operations, contrastedBaseAA);

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

            RoundupOperations(operations, supaDetailTag);

            MergeLayersFromFiles(operations, supportsTag);

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
            
            RoundupOperations(operations, supaDetailSupportedTag + "_" + DateTime.UtcNow.ToString("yyMMddHHmm"));

            return !Progress.Token.IsCancellationRequested;
        }

        private string CreateDiffuse(string elementTag, uint modelSkinDepth, byte brightnessFalloff, byte skinBrightnessFalloff, uint subsurfaceDepth, uint corrosionDepth, uint featureDepth)
        {
            List<Operation> operations = new();

            // prepare base element
            Progress.Title = "Creating diffuse " + elementTag;
            ReplaceActiveFile(operations, elementTag);

            operations.Add(new OperationMorph(SlicerFile)
            {
                MorphOperation = OperationMorph.MorphOperations.IsolateFeatures,
                Iterations = featureDepth,
                LayerIndexStart = 0,
                LayerIndexEnd = SlicerFile.LastLayerIndex,
                LayerRangeSelection = Enumerations.LayerRangeSelection.All
            });

            RoundupOperations(operations, elementTag + "_features");

            ReplaceActiveFile(operations, elementTag);

            if (modelSkinDepth > 0)
            {
                // extend model by skin depth
                operations.Add(new OperationMorph(SlicerFile)
                {
                    MorphOperation = OperationMorph.MorphOperations.Dilate,
                    Iterations = modelSkinDepth,
                    LayerIndexStart = 0,
                    LayerIndexEnd = SlicerFile.LastLayerIndex,
                    LayerRangeSelection = Enumerations.LayerRangeSelection.All
                });

                // fade skin
                for (uint i = 1; i <= modelSkinDepth + 1; i++)
                {
                    uint wallThickness = i;
                    SubtractFromWalls(operations, skinBrightnessFalloff, wallThickness);
                }

                // restore original model voxels
                MergeLayersFromFiles(operations, elementTag);
            }

            if (subsurfaceDepth > 0)
            {
                // fade subsurface (increases probability that corrosion will remove material pixels)
                uint wallThickness = modelSkinDepth + subsurfaceDepth;
                SubtractFromWalls(operations, brightnessFalloff, wallThickness);
            }

            if (corrosionDepth > 0)
            {
                // fade corrosion depth (increases probability that corrosion will remove material pixels)
                uint wallThickness = modelSkinDepth + subsurfaceDepth + corrosionDepth;
                SubtractFromWalls(operations, brightnessFalloff, wallThickness);
            }

            if (ApplyCorrosion.Value)
            {

                if (modelSkinDepth > 0)
                {
                    //corrode model skin (NB will be further corroded by subsurface and corrosion depth passes)
                    operations.Add(new OperationPixelArithmetic(SlicerFile)
                    {
                        Operator = PixelArithmeticOperators.Corrode,
                        ApplyMethod = PixelArithmeticApplyMethod.ModelWalls,
                        WallThickness = modelSkinDepth,
                        NoiseThreshold = 0,
                        NoiseMinOffset = FirstPassNoiseMinimumOffset.Value,
                        NoiseMaxOffset = FirstPassNoiseMaximumOffset.Value
                    });
                }

                if (subsurfaceDepth > 0)
                {
                    // corrode skin and subsurface (NB will be further corroded by corrosion depth pass)
                    operations.Add(new OperationPixelArithmetic(SlicerFile)
                    {
                        Operator = PixelArithmeticOperators.Corrode,
                        ApplyMethod = PixelArithmeticApplyMethod.ModelWalls,
                        WallThickness = modelSkinDepth + subsurfaceDepth,
                        NoiseThreshold = 0,
                        NoiseMinOffset = SecondPassNoiseMinimumOffset.Value,
                        NoiseMaxOffset = SecondPassNoiseMaximumOffset.Value
                    });
                }

                if (corrosionDepth > 0)
                {
                    // corrode all corrosion features (skin, subsurface and corrosion depth)
                    operations.Add(new OperationPixelArithmetic(SlicerFile)
                    {
                        Operator = PixelArithmeticOperators.Corrode,
                        ApplyMethod = PixelArithmeticApplyMethod.ModelWalls,
                        WallThickness = modelSkinDepth + subsurfaceDepth + corrosionDepth,
                        NoiseThreshold = 0,
                        NoiseMinOffset = ThirdPassNoiseMinimumOffset.Value,
                        NoiseMaxOffset = ThirdPassNoiseMaximumOffset.Value
                    });
                }
            }

            MergeLayersFromFiles(operations, elementTag + "_features");

            Progress.Title = "Diffusing " + elementTag;
            var outputTag = elementTag + "_diffused";
            RoundupOperations(operations, outputTag);

            return outputTag;
        }

        private void SubtractFromWalls(List<Operation> operations, byte brightnessFalloff, uint wallThickness)
        {
            operations.Add(new OperationPixelArithmetic(SlicerFile)
            {
                Operator = PixelArithmeticOperators.Subtract,
                ApplyMethod = PixelArithmeticApplyMethod.ModelWalls,
                UsePattern = false,
                Value = brightnessFalloff,
                WallThickness = wallThickness
            });
        }

        private void MergeLayersFromFiles(List<Operation> operations, params string[] elementTags)
        {
            OperationLayerImport restoreElementDetailFeatures = new(SlicerFile)
            {
                ImportType = ImportTypes.MergeMax
            };

            foreach (var elementTag in elementTags)
            {
                restoreElementDetailFeatures.AddFile(CompositionFileName(elementTag));
            }
            operations.Add(restoreElementDetailFeatures);
        }

        private void ReplaceActiveFile(List<Operation> operations, string elementTag)
        {
            OperationLayerImport importBaseElement = new(SlicerFile)
            {
                ImportType = ImportTypes.Replace
            };
            importBaseElement.AddFile(CompositionFileName(elementTag));

            operations.Add(importBaseElement);
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

            var intermediateTag = diffusedElementTag + "_contrasted_intermediate";
            RoundupOperations(operations, intermediateTag);

            // cutters and original (to be restored)
            operations = new();

            OperationPixelArithmetic clearSlices = new(SlicerFile)
            {
                Operator = PixelArithmeticOperators.Set,
                Value = 0,
                ApplyMethod = PixelArithmeticApplyMethod.All,
            };
            operations.Add(clearSlices);

            MergeLayersFromFiles(operations, cutters);

            OperationLayerImport importElement = new(SlicerFile)
            {
                ImportType = ImportTypes.BitwiseAnd
            };
            importElement.AddFile(CompositionFileName(elementTag));
            operations.Add(importElement);

            MergeLayersFromFiles(operations, intermediateTag);

            var newTag = diffusedElementTag + "_contrasted";

            RoundupOperations(operations, newTag);
            
            return newTag;
        }

        private void RoundupOperations(List<Operation> operations, string saveIntermediateState = null)
        {
            foreach (var operation in operations)
            {
                Progress.Token.ThrowIfCancellationRequested();
                if (!operation.CanValidate()) continue;
                operation.Execute(Progress);
            }

            if (saveIntermediateState != null)
            {
                SlicerFile.SaveAs(CompositionFileName(saveIntermediateState), Progress);
            }

            operations.Clear();
        }

        private string CreateCutter(string elementTag, uint dilationPixels)
        {
            List<Operation> operations = new();

            ReplaceActiveFile(operations, elementTag);

             OperationMorph dilateFlatDetail = new(SlicerFile)
            {
                MorphOperation = OperationMorph.MorphOperations.Dilate,
                Iterations = dilationPixels,
                LayerIndexStart = 0,
                LayerIndexEnd = SlicerFile.LastLayerIndex,
                LayerRangeSelection = Enumerations.LayerRangeSelection.All

            };
            operations.Add(dilateFlatDetail);

            var newTag = elementTag + "_dilated";
            RoundupOperations(operations, newTag);

            return newTag;
        }
    }
}
