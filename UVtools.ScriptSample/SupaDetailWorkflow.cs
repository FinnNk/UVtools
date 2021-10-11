using System;
using System.Collections.Generic;
using System.IO;
using UVtools.Core;
using UVtools.Core.Operations;
using UVtools.Core.Scripting;
using static UVtools.Core.Operations.OperationLayerImport;
using static UVtools.Core.Operations.OperationPixelArithmetic;

namespace UVtools.ScriptSample
{
    public class SupaDetailWorkflow : ScriptGlobals
    {
        public void ScriptInit()
        {
            Script.Name = "Automate SupaDetail! workflow";
            Script.Description = "A workflow automation to combine selective AA, countersunk voids, surface corrosion, etc";
            Script.Author = "Finn Neuik";
            Script.Version = new Version(0, 1);
            Script.UserInputs.AddRange(new ScriptBaseInput[] { });
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
            return null;
        }

        public bool ScriptExecute()
        {
            List<Operation> operations = new();

            var dilatedDetailCutter = CreateCutter("detail", 3);
            var dilatedFlatDetailCutter = CreateCutter("flat detail", 3);
            var dilatedFlatCutter = CreateCutter("flat", 1);
            var contrastedDetailAA = CreateContrasted("detail aa", "detail", dilatedFlatCutter, dilatedFlatDetailCutter);
            var contrastedFlatDetailAA = CreateContrasted("flat detail aa", "flat detail", dilatedFlatCutter, dilatedDetailCutter);
            var contrastedFlatAA = CreateContrasted("flat aa", "flat", dilatedFlatDetailCutter, dilatedDetailCutter);

            operations = new();

            if (SlicerFile.FileFullPath != CompositionFileName(contrastedFlatAA))
            {
                OperationLayerImport importFlatAAElement = new(SlicerFile)
                {
                    ImportType = ImportTypes.Replace
                };
                importFlatAAElement.AddFile(CompositionFileName(contrastedFlatAA));
                operations.Add(importFlatAAElement);
            }

            OperationLayerImport importDetailAAElements = new(SlicerFile)
            {
                ImportType = ImportTypes.MergeMax
            };
            importDetailAAElements.AddFile(CompositionFileName(contrastedDetailAA));
            importDetailAAElements.AddFile(CompositionFileName(contrastedFlatDetailAA));
            operations.Add(importDetailAAElements);

            OperationPixelArithmetic corrode = new(SlicerFile)
            {
                Operator = OperationPixelArithmetic.PixelArithmeticOperators.Corrode,
                ApplyMethod = PixelArithmeticApplyMethod.All,
                NoiseThreshold = 0,
                NoiseMinOffset = -128,
                NoiseMaxOffset = 128
            };
            operations.Add(corrode);

            //reinstate cores
            OperationLayerImport restoreCores = new(SlicerFile)
            {
                ImportType = ImportTypes.MergeMax
            };
            restoreCores.AddFile(CompositionFileName("flat"));
            restoreCores.AddFile(CompositionFileName("detail"));
            restoreCores.AddFile(CompositionFileName("flat detail"));
            operations.Add(restoreCores);

            OperationLayerImport subtractVoidElement = new(SlicerFile)
            {
                ImportType = ImportTypes.Subtract
            };
            subtractVoidElement.AddFile(CompositionFileName("voids"));
            operations.Add(subtractVoidElement);

            RoundupOperations(operations);

            SlicerFile.SaveAs(CompositionFileName("supadetail"), Progress);

            operations = new();

            OperationLayerImport importSupports = new(SlicerFile)
            {
                ImportType = ImportTypes.MergeMax
            };
            importSupports.AddFile(CompositionFileName("supports"));
            operations.Add(importSupports);

            OperationRepairLayers removeEmpty = new(SlicerFile)
            {
                RemoveEmptyLayers = true,
                RepairIslands = false,
                RepairResinTraps = false,
                RepairSuctionCups = false
            };
            operations.Add(removeEmpty);

            RoundupOperations(operations);

            SlicerFile.SaveAs(CompositionFileName("supadetail_supported"), Progress);

            return !Progress.Token.IsCancellationRequested;
        }

        private string CreateContrasted(string baseFileTagAA, string baseFileTag, params string[] cutters)
        {
            List<Operation> operations = new();

            OperationLayerImport importFlatDetailElement = new(SlicerFile)
            {
                ImportType = ImportTypes.Replace
            };
            importFlatDetailElement.AddFile(CompositionFileName(baseFileTagAA));
            operations.Add(importFlatDetailElement);

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

        private string CreateCutter(string baseFileTag, uint iter = 2)
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
                Iterations = iter,
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
