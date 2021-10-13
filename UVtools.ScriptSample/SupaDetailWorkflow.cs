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
    public class SupaDetailWorkflow : ScriptGlobals
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
            Value = 85
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
            Value = true
        };

        ScriptNumericalInput<byte> BinaryThreshold = new()
        {
            Label = "Binary Threshold", //todo: replace with quantisation perhaps
            ToolTip = "Binary threshold to apply to final slices",
            Minimum = byte.MinValue,
            Maximum = byte.MaxValue,
            Increment = 1,
            Value = 119
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
            Script.Version = new Version(0, 1);
            Script.UserInputs.AddRange(new ScriptBaseInput[] {
                NoiseMinimumOffset,
                NoiseMaximumOffset,
                PixelsOfAAToRemove,
                ApplyThreshold,
                BinaryThreshold,
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
            var baseFiles = new List<string> { baseTag, semisharpDetailTag, softDetailTag, sharpDetailTag, voidsTag, supportsTag }
                .Select(CompositionFileName)
                .Where(f => !File.Exists(f));

            var aaFiles = new List<string> { baseTag, semisharpDetailTag, softDetailTag }
                .Select(f => CompositionFileName(f + "_aa"))
                .Where(f => !File.Exists(f));

            var missingFiles = baseFiles.Union(aaFiles);

            if (!missingFiles.Any())
            {
                return null;
            }

            return "Missing the following files:" + String.Join(Environment.NewLine, missingFiles);
        }

        public bool ScriptExecute()
        {
            //todo: very inefficient, look to load in matrices and only save when intermediate results are useful

            // create tooling - these are used to remove AA from surrounding elements
            Progress.Title = "Creating cutters";
            var dilatedSharpDetailCutter = CreateCutter(sharpDetailTag, PixelsOfAAToRemove.Value);
            var dilatedSoftDetailCutter = CreateCutter(softDetailTag, PixelsOfAAToRemove.Value);
            var dilatedSemisharpDetailCutter = CreateCutter(semisharpDetailTag, PixelsOfAAToRemove.Value);
            var dilatedBaseCutter = CreateCutter(baseTag, PixelsOfAAToRemove.Value);
            var dilatedSupportsCutter = CreateCutter(supportsTag, 2);

            // create tooling - restore original model around touching elements
            Progress.Title = "Creating base model contact restorer";
            var baseModelRestorer = CreateContactRestorer(baseTag, dilatedSharpDetailCutter, dilatedSoftDetailCutter, dilatedSemisharpDetailCutter, dilatedSupportsCutter);

            // create tooling - use cutters to remove material from surrounding models that have AA, significantly enhancing sharpness
            Progress.Title = "Creating contrasted elements";
            var contrastedLightDetailAA = CreateContrasted(semisharpDetailTag, dilatedBaseCutter, dilatedSoftDetailCutter, dilatedSharpDetailCutter);
            var contrastedFlatDetailAA = CreateContrasted(softDetailTag, dilatedBaseCutter, dilatedSemisharpDetailCutter, dilatedSharpDetailCutter);
            var contrastedFlatAA = CreateContrasted(baseTag, dilatedSoftDetailCutter, dilatedSemisharpDetailCutter, dilatedSharpDetailCutter);


            List<Operation> operations = new();

            // compose the various elements
            OperationLayerImport importFlatAAElement = new(SlicerFile)
            {
                ImportType = ImportTypes.Replace
            };
            importFlatAAElement.AddFile(CompositionFileName(contrastedFlatAA));
            operations.Add(importFlatAAElement);

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
            restoreCores.AddFile(CompositionFileName(sharpDetailTag));
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

            // apply thresholding
            if (ApplyThreshold.Value)
            {
                OperationPixelArithmetic threshold = new(SlicerFile)
                {
                    Operator = PixelArithmeticOperators.Threshold,
                    ApplyMethod = PixelArithmeticApplyMethod.All,
                    UsePattern = false,
                    Value = BinaryThreshold.Value,
                    ThresholdMaxValue = 255,
                    ThresholdType = ThresholdType.Binary
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

            Progress.Title = "Final processing - supports and thresholding";
            RoundupOperations(operations);

            SlicerFile.SaveAs(CompositionFileName(supaDetailSupportedTag), Progress);

            return !Progress.Token.IsCancellationRequested;
        }

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

            // remove 1 pixel from base model so corrosion bites into surface
            uint biteOfCorrosionIntoModel = 1; // pixels, should be able to achieve this with wall settings
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

            RoundupOperations(operations);

            var restorerTag = baseFileTag + "_restorer";

            SlicerFile.SaveAs(CompositionFileName(restorerTag), Progress);

            return restorerTag;
        }

        private string CreateContrasted(string baseFileTag, params string[] cutters)
        {
            string baseFileTagAA = baseFileTag + "_aa";
            List<Operation> operations = new();

            OperationLayerImport importBaseElement = new(SlicerFile)
            {
                ImportType = ImportTypes.Replace
            };
            importBaseElement.AddFile(CompositionFileName(baseFileTagAA));
            operations.Add(importBaseElement);

            OperationLayerImport subtractElements = new(SlicerFile)
            {
                ImportType = ImportTypes.Subtract
            };
            foreach (var cutter in cutters)
            {

                subtractElements.AddFile(CompositionFileName(cutter));
            }
            operations.Add(subtractElements);

            OperationLayerImport restoreElements = new(SlicerFile)
            {
                ImportType = ImportTypes.MergeMax
            };
            restoreElements.AddFile(CompositionFileName(baseFileTag));
            operations.Add(restoreElements);

            RoundupOperations(operations);

            var newTag = baseFileTagAA + "_contrasted";

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

        private string CreateCutter(string baseFileTag, uint dilationPixels)
        {
            List<Operation> operations = new();

            OperationLayerImport importFlatDetailElement = new(SlicerFile)
            {
                ImportType = ImportTypes.Replace
            };
            importFlatDetailElement.AddFile(CompositionFileName(baseFileTag));
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

            var newTag = baseFileTag + "_dilated";

            SlicerFile.SaveAs(CompositionFileName(newTag), Progress);

            return newTag;
        }
    }
}
