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

        ScriptNumericalInput<short> NoiseMinimumOffset = new()
        {
            Label ="Minimum noise offset",
            ToolTip = "Lower bound of uniform noise to dither surface",
            Minimum = -1000,
            Maximum = 1000,
            Increment = 1,
            Value = -85
        };

        ScriptNumericalInput<short> NoiseMaximumOffset = new()
        {
            Label = "Maximum noise offset",
            ToolTip = "Upper bound of uniform noise to dither surface",
            Minimum = -1000,
            Maximum = 1000,
            Increment = 1,
            Value = 20
        };

        ScriptNumericalInput<byte> PixelsOfAAToRemove = new()
        {
            Label = "Pixels of AA to remove", //todo: replace with quantisation perhaps
            ToolTip = "Enhance contrast by removing this many pixels of AA from touching elements",
            Minimum = 1,
            Maximum = 4,
            Increment = 1,
            Value = 3
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

        ScriptNumericalInput<uint> CorrosionBite = new()
        {
            Label = "Corrosion bite", //todo: replace with quantisation perhaps
            ToolTip = "Number of pixels from surface to corrode - should be able to use wall here!",
            Minimum = byte.MinValue,
            Maximum = 4,
            Increment = 1,
            Value = 4
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
                NoiseMinimumOffset,
                NoiseMaximumOffset,
                PixelsOfAAToRemove,
                ApplyThreshold,
                ToZeroThreshold,
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
            //todo: create single/small numberer layer play files

            Progress.Title = "Creating diffused elements";
            var diffusedBase = CreateDiffuse(baseTag, 1, 40, 220, 1, 3);
            var diffusedSoft = CreateDiffuse(softDetailTag, 1, 40, 220, 1, 3);
            var diffusedSemisharp = CreateDiffuse(semisharpDetailTag, 1, 20, 220, 1, 1);

            Progress.Title = "Creating cutters";
            var dilatedSharpDetailCutter = CreateCutter(sharpDetailTag, PixelsOfAAToRemove.Value);
            var dilatedSoftDetailCutter = CreateCutter(softDetailTag, PixelsOfAAToRemove.Value);
            var dilatedSemisharpDetailCutter = CreateCutter(semisharpDetailTag, PixelsOfAAToRemove.Value);
            var dilatedBaseCutter = CreateCutter(baseTag, PixelsOfAAToRemove.Value);
            var dilatedSupportsCutter = CreateCutter(supportsTag, 2);

            // create tooling - restore original base model around touching elements
            Progress.Title = "Creating base model contact restorer";
            // not sure if this is needed any more? may want to think about restoring fully anything that's an intersection
            var baseModelRestorer = CreateContactRestorer(baseTag, dilatedSharpDetailCutter, dilatedSoftDetailCutter, dilatedSemisharpDetailCutter, dilatedSupportsCutter);

            // create tooling - use cutters to remove material from surrounding models that have AA, significantly enhancing sharpness
            Progress.Title = "Creating contrasted elements";
            var contrastedSemisharpDetailAA = CreateContrasted(semisharpDetailTag, diffusedSemisharp, dilatedBaseCutter, dilatedSoftDetailCutter, dilatedSharpDetailCutter);
            var contrastedSoftDetailAA = CreateContrasted(softDetailTag, diffusedSoft, dilatedBaseCutter, dilatedSemisharpDetailCutter, dilatedSharpDetailCutter);
            var contrastedBaseAA = CreateContrasted(baseTag, diffusedBase, dilatedSoftDetailCutter, dilatedSemisharpDetailCutter, dilatedSharpDetailCutter);

            //compose model elements
            Progress.Title = "Composing model elements";
            List<Operation> operations = new();

            OperationLayerImport importBaseElement = new(SlicerFile)
            {
                ImportType = ImportTypes.Replace,
            };
            importBaseElement.AddFile(CompositionFileName(baseModelRestorer));
            operations.Add(importBaseElement);

            OperationLayerImport restoreCores = new(SlicerFile)
            {
                ImportType = ImportTypes.MergeMax
            };
            restoreCores.AddFile(CompositionFileName(contrastedSoftDetailAA));
            restoreCores.AddFile(CompositionFileName(contrastedSemisharpDetailAA));
            restoreCores.AddFile(CompositionFileName(contrastedBaseAA));
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

            var outputTag = supaDetailTag;//  String.Join('_', supaDetailTag, SlicerFile.LayerHeight,/* SlicerFile.ExposureRepresentation,*/ DateTime.UtcNow.ToString("yyMMdd_HHmm"));
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
            SlicerFile.SaveAs(CompositionFileName(supaDetailSupportedTag), Progress);

            return !Progress.Token.IsCancellationRequested;

        }

        private string CreateDiffuse(string elementTag, uint modelSkinDepth, byte brightnessFalloff, byte skinBrightness = 180, uint subsurfaceDepth = 1, uint corrosionDepth = 3) {
            List<Operation> operations = new();

            // prepare base element
            Progress.Title = "Creating diffuse " + elementTag;
            OperationLayerImport importBaseElement = new(SlicerFile)
            {
                ImportType = ImportTypes.Replace
            };
            importBaseElement.AddFile(CompositionFileName(elementTag));
            operations.Add(importBaseElement);

            uint featureDepth = 8; // pixels, should be able to achieve this with wall settings
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

            OperationPixelArithmetic setModelWallBrightness = new(SlicerFile)
            {
                Operator = PixelArithmeticOperators.Subtract,
                ApplyMethod = PixelArithmeticApplyMethod.ModelWalls,
                UsePattern = false,
                Value = brightnessFalloff,
                WallThickness = subsurfaceDepth
            };
            operations.Add(setModelWallBrightness);

            OperationPixelArithmetic setModelWallBrightness2 = new(SlicerFile)
            {
                Operator = PixelArithmeticOperators.Subtract,
                ApplyMethod = PixelArithmeticApplyMethod.ModelWalls,
                UsePattern = false,
                Value = brightnessFalloff,
                WallThickness = corrosionDepth
            };
            operations.Add(setModelWallBrightness2);

            OperationPixelArithmetic corrode = new(SlicerFile)
            {
                Operator = PixelArithmeticOperators.Corrode,
                ApplyMethod = PixelArithmeticApplyMethod.ModelWalls,
                WallThickness = 1,
                NoiseThreshold = 0,
                NoiseMinOffset = (short) (NoiseMinimumOffset.Value / 3),
                NoiseMaxOffset = (short) (NoiseMaximumOffset.Value / 3)
            };
            operations.Add(corrode);

            OperationPixelArithmetic corrode2 = new(SlicerFile)
            {
                Operator = PixelArithmeticOperators.Corrode,
                ApplyMethod = PixelArithmeticApplyMethod.ModelWalls,
                WallThickness = subsurfaceDepth,
                NoiseThreshold = 0,
                NoiseMinOffset = (short)(NoiseMinimumOffset.Value / 3),
                NoiseMaxOffset = (short)(NoiseMaximumOffset.Value / 3)
            };
            operations.Add(corrode2);

            RoundupOperations(operations);

            OperationPixelArithmetic corrode3 = new(SlicerFile)
            {
                Operator = PixelArithmeticOperators.Corrode,
                ApplyMethod = PixelArithmeticApplyMethod.ModelWalls,
                WallThickness = corrosionDepth,
                NoiseThreshold = 0,
                NoiseMinOffset = (short)(NoiseMinimumOffset.Value / 3),
                NoiseMaxOffset = (short)(NoiseMaximumOffset.Value / 3)
            };
            operations.Add(corrode3);

            OperationLayerImport restoreBaseFeatures = new(SlicerFile)
            {
                ImportType = ImportTypes.MergeMax
            };
            restoreBaseFeatures.AddFile(CompositionFileName(elementTag + "_features"));
            operations.Add(restoreBaseFeatures);

            Progress.Title = "Diffusing " + elementTag;
            RoundupOperations(operations);

            var outputTag = elementTag + "_diffused";
            SlicerFile.SaveAs(CompositionFileName(outputTag), Progress);

            return outputTag;
        }

/*

            OperationLayerImport importDetailAAElements = new(SlicerFile)
            {
                ImportType = ImportTypes.MergeMax
            };
            importDetailAAElements.AddFile(CompositionFileName(contrastedLightDetailAA));
            importDetailAAElements.AddFile(CompositionFileName(contrastedFlatDetailAA));
            operations.Add(importDetailAAElements);

            // Apply diffusion to non-void pixels
            OperationPixelArithmetic corrode = new(SlicerFile)
            {
                Operator = PixelArithmeticOperators.Corrode,
                ApplyMethod = PixelArithmeticApplyMethod.All,
                NoiseThreshold = 0,
                NoiseMinOffset = NoiseMinimumOffset.Value,
                NoiseMaxOffset = NoiseMaximumOffset.Value
            };
            operations.Add(corrode);

            //reinstate cores
            OperationLayerImport restoreCores = new(SlicerFile)
            {
                ImportType = ImportTypes.MergeMax
            };
            restoreCores.AddFile(CompositionFileName(baseModelRestorer));
            restoreCores.AddFile(CompositionFileName(softDetailTag));
            restoreCores.AddFile(CompositionFileName(semisharpDetailTag));
            operations.Add(restoreCores);

            //reinstate voids
            OperationLayerImport subtractVoidElement = new(SlicerFile)
            {
                ImportType = ImportTypes.Subtract
            };
            subtractVoidElement.AddFile(CompositionFileName(voidsTag));
            operations.Add(subtractVoidElement);

            //reinstate sharp detail
            OperationLayerImport restoreSharp = new(SlicerFile)
            {
                ImportType = ImportTypes.MergeMax
            };
            restoreSharp.AddFile(CompositionFileName(sharpDetailTag));
            operations.Add(restoreSharp);

            Progress.Title = "Composing elements";
            RoundupOperations(operations);

            SlicerFile.SaveAs(CompositionFileName(supaDetailTag), Progress);

            operations = new();
            // add supports - done last to allow for independent adjustment
            OperationLayerImport importSupports = new(SlicerFile)
            {
                ImportType = ImportTypes.MergeMax
            };
            importSupports.AddFile(CompositionFileName(supportsTag));
            operations.Add(importSupports);



            Progress.Title = "Final processing - supports and thresholding";
            RoundupOperations(operations);

            var outputTag = String.Join('_', supaDetailSupportedTag, SlicerFile.LayerHeight, SlicerFile.ExposureRepresentation, DateTime.UtcNow.ToString("yyMMdd_HHmm"));
            SlicerFile.SaveAs(CompositionFileName(outputTag), Progress);

            return !Progress.Token.IsCancellationRequested;
        }
*/
        private string CreateContactRestorer(string baseFileTag, params string[] cutters)
        {
            List<Operation> operations = new();

            OperationPixelArithmetic clearSlices = new(SlicerFile)
            {
                Operator = PixelArithmeticOperators.Set,
                Value = 0,
                ApplyMethod = PixelArithmeticApplyMethod.All,
            };
            operations.Add(clearSlices);

            OperationLayerImport importCutterElements = new(SlicerFile)
            {
                ImportType = ImportTypes.MergeMax
            };
            foreach (var cutter in cutters)
            {
                importCutterElements.AddFile(CompositionFileName(cutter));
            }
            operations.Add(importCutterElements);

            OperationLayerImport intersectBaseElement = new(SlicerFile)
            {
                ImportType = ImportTypes.BitwiseAnd
            };
            intersectBaseElement.AddFile(CompositionFileName(baseFileTag));
            
            operations.Add(intersectBaseElement);

            Progress.Title = "Contct restorer " + baseFileTag;

            RoundupOperations(operations);

            var contactRestorerTag = baseFileTag + "_contact_restorer";

            SlicerFile.SaveAs(CompositionFileName(contactRestorerTag), Progress);

            operations = new();

            // load the base model without AA in order to override any noise and/or artifacts
            intersectBaseElement = new(SlicerFile)
            {
                ImportType = ImportTypes.Replace
            };
            intersectBaseElement.AddFile(CompositionFileName(baseFileTag));
            operations.Add(intersectBaseElement);

            // remove skin from base model so when used for restoration corrosion bites into surface
            uint biteOfCorrosionIntoModel = CorrosionBite.Value; // pixels, should be able to achieve this with wall settings
            OperationMorph erodeBaseElement = new(SlicerFile)
            {
                MorphOperation = OperationMorph.MorphOperations.Erode,
                Iterations = biteOfCorrosionIntoModel,
                LayerIndexStart = 0,
                LayerIndexEnd = SlicerFile.LastLayerIndex,
                LayerRangeSelection = Enumerations.LayerRangeSelection.All
            };
            operations.Add(erodeBaseElement);

            // restore areas where other elements touched the base model - especially important for supports,
            // but generally not doing this can result in unwanted voids or weaknesses
            intersectBaseElement = new(SlicerFile)
            {
                ImportType = ImportTypes.MergeMax
            };
            intersectBaseElement.AddFile(CompositionFileName(contactRestorerTag));
            operations.Add(intersectBaseElement);

            Progress.Title = "Creating restorer " + baseFileTag;

            RoundupOperations(operations);

            var restorerTag = baseFileTag + "_restorer";

            SlicerFile.SaveAs(CompositionFileName(restorerTag), Progress);

            return restorerTag;
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
