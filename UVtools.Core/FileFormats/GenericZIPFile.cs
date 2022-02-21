﻿/*
 *                     GNU AFFERO GENERAL PUBLIC LICENSE
 *                       Version 3, 19 November 2007
 *  Copyright (C) 2007 Free Software Foundation, Inc. <https://fsf.org/>
 *  Everyone is permitted to copy and distribute verbatim copies
 *  of this license document, but changing it is not allowed.
 */

using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Serialization;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Emgu.CV.Util;
using UVtools.Core.Extensions;
using UVtools.Core.Layers;
using UVtools.Core.Operations;

namespace UVtools.Core.FileFormats
{
    #region Sub Classes
    [Serializable]
    [XmlRoot(ElementName = "Manifest")]
    public class GenericZipManifest
    {
        public string CreatedBy { get; set; } = About.SoftwareWithVersion;
        
        public string UpdatedBy { get; set; } = About.SoftwareWithVersion;

        public string CreatedDate { get; set; } = DateTime.UtcNow.ToString("u");

        public string LastModifiedDate { get; set; } = DateTime.UtcNow.ToString("u");

        public float LayerHeight { get; set; }

        public ushort ResolutionX { get; set; }

        public ushort ResolutionY { get; set; }

        public float DisplayWidth { get; set; }

        public float DisplayHeight { get; set; }

        public float MachineZ { get; set; }

        public void Update()
        {
            UpdatedBy = About.SoftwareWithVersion;
            LastModifiedDate = DateTime.UtcNow.ToString("u");
        }
    }
    #endregion

    public class GenericZIPFile : FileFormat
    {
        #region Constants
        private const string ManifestFileName = "manifest.uvtools";
        #endregion

        #region Properties
        public GenericZipManifest ManifestFile { get; set; } = new ();

        public override FileFormatType FileType => FileFormatType.Archive;

        public override FileExtension[] FileExtensions { get; } = {
            new(typeof(GenericZIPFile), "zip", "Generic / Phrozen Zip")
        };

        public override uint ResolutionX
        {
            get => ManifestFile.ResolutionX;
            set
            {
                ManifestFile.ResolutionX = (ushort) value;
                RaisePropertyChanged();
            }
        }

        public override uint ResolutionY
        {
            get => ManifestFile.ResolutionY;
            set
            {
                ManifestFile.ResolutionY = (ushort)value;
                RaisePropertyChanged();
            }
        }

        public override float DisplayWidth
        {
            get => ManifestFile.DisplayWidth;
            set
            {
                ManifestFile.DisplayWidth = value;
                RaisePropertyChanged();
            }
        }

        public override float DisplayHeight
        {
            get => ManifestFile.DisplayHeight;
            set
            {
                ManifestFile.DisplayHeight = value;
                RaisePropertyChanged();
            }
        }

        public override float MachineZ
        {
            get => ManifestFile.MachineZ > 0 ? ManifestFile.MachineZ : base.MachineZ;
            set
            {
                ManifestFile.MachineZ = value;
                RaisePropertyChanged();
            }
        }

        public override float LayerHeight
        {
            get => ManifestFile.LayerHeight;
            set
            {
                ManifestFile.LayerHeight = Layer.RoundHeight(value);
                RaisePropertyChanged();
            }
        }

        /*public override uint LayerCount
        {
            get => base.LayerCount;
            set => base.LayerCount = ManifestFile.Slices.LayerCount = base.LayerCount;
        }*/

        public override Size[] ThumbnailsOriginalSize { get; } =
        {
            new(854, 480),
            new(472, 240)
        };


        public override object[] Configs => new object[] { 
            ManifestFile
        };

        #endregion

        #region Constructor
        public GenericZIPFile()
        { }
        #endregion

        #region Methods

        public override bool CanProcess(string fileFullPath)
        {
            if(!base.CanProcess(fileFullPath)) return false;

            try
            {
                using var zip = ZipFile.Open(fileFullPath, ZipArchiveMode.Read);
                foreach (var entry in zip.Entries)
                {
                    if (entry.Name == ManifestFileName) return true;
                    if (entry.Name.EndsWith(".gcode")) return false;
                }
            }
            catch (Exception e)
            {
                Debug.WriteLine(e);
                return false;
            }
            

            return true;
        }

        protected override void EncodeInternally(OperationProgress progress)
        {
            using var outputFile = ZipFile.Open(FileFullPath, ZipArchiveMode.Create);
            if (Thumbnails.Length > 0 && Thumbnails[0] is not null)
            {
                using var stream = outputFile.CreateEntry("preview.png").Open();
                stream.WriteBytes(Thumbnails[0].GetPngByes());
                stream.Close();
            }

            if (Thumbnails.Length > 1 && Thumbnails[1] is not null)
            {
                using var stream = outputFile.CreateEntry("preview_cropping.png").Open();
                using var vec = new VectorOfByte();
                stream.WriteBytes(Thumbnails[1].GetPngByes());
                stream.Close();
            }

            for (uint layerIndex = 0; layerIndex < LayerCount; layerIndex++)
            {
                progress.Token.ThrowIfCancellationRequested();
                var layer = this[layerIndex];
                var filename = $"{layerIndex + 1}.png";
                outputFile.PutFileContent(filename, layer.CompressedBytes, ZipArchiveMode.Create);
                progress++;
            }

            ManifestFile.Update();

            XmlSerializer serializer = new(ManifestFile.GetType());
            XmlSerializerNamespaces ns = new();
            ns.Add("", "");
            var entry = outputFile.CreateEntry(ManifestFileName);
            using var streamManifest = entry.Open();
            serializer.Serialize(streamManifest, ManifestFile, ns);
        }

        protected override void DecodeInternally(OperationProgress progress)
        {
            using (var inputFile = ZipFile.Open(FileFullPath, ZipArchiveMode.Read))
            {
                var entry = inputFile.Entries.FirstOrDefault(zipEntry => zipEntry.Name == ManifestFileName);
                if (entry is not null)
                {
                    try
                    {
                        var serializer = new XmlSerializer(ManifestFile.GetType());
                        using var stream = entry.Open();
                        ManifestFile = (GenericZipManifest) serializer.Deserialize(stream);
                    }
                    catch (Exception)
                    {
                        // Not required
                        //Clear();
                        //throw new FileLoadException($"Unable to deserialize '{entry.Name}'\n{e}", FileFullPath);
                    }
                }

                uint layerCount = 0;
                foreach (var zipEntry in inputFile.Entries)
                {
                    if (!zipEntry.Name.EndsWith(".png")) continue;
                    var filename = Path.GetFileNameWithoutExtension(zipEntry.Name);
                    if (!filename.All(char.IsDigit)) continue;
                    if (!uint.TryParse(filename, out var layerIndex)) continue;
                    layerCount = Math.Max(layerCount, layerIndex);
                }

                if (layerCount == 0)
                {
                    Clear();
                    throw new FileLoadException("Unable to detect layer images in the file", FileFullPath);
                }

                LayerManager.Init(layerCount, DecodeType == FileDecodeType.Partial);
                progress.Reset(OperationProgress.StatusDecodeLayers, LayerCount);


                for (uint layerIndex = 0; layerIndex < LayerCount; layerIndex++)
                {
                    if (progress.Token.IsCancellationRequested) break;
                    var filename = $"{layerIndex + 1}.png";
                    entry = inputFile.GetEntry(filename);
                    if (entry is null)
                    {
                        Clear();
                        throw new FileLoadException($"Layer {filename} not found", FileFullPath);
                    }

                    if (DecodeType == FileDecodeType.Full)
                    {
                        using var stream = entry.Open();
                        this[layerIndex] = new Layer(layerIndex, stream, LayerManager);
                    }

                    progress++;
                }

                entry = inputFile.GetEntry("preview.png");
                if (entry is not null)
                {
                    Thumbnails[0] = new Mat();
                    CvInvoke.Imdecode(entry.Open().ToArray(), ImreadModes.AnyColor, Thumbnails[0]);
                }

                entry = inputFile.GetEntry("preview_cropping.png");
                if (entry is not null)
                {
                    var count = CreatedThumbnailsCount;
                    Thumbnails[count] = new Mat();
                    CvInvoke.Imdecode(entry.Open().ToArray(), ImreadModes.AnyColor, Thumbnails[count]);
                }
            }

            LayerManager.GetBoundingRectangle(progress);
        }

        protected override void PartialSaveInternally(OperationProgress progress)
        {
            using var outputFile = ZipFile.Open(FileFullPath, ZipArchiveMode.Update);
            bool deleted;

            do
            {
                deleted = false;
                foreach (var zipEntry in outputFile.Entries)
                {
                    if (zipEntry.Name != ManifestFileName) continue;
                    zipEntry.Delete();
                    deleted = true;
                    break;
                }
            } while (deleted);

            ManifestFile.Update();
            
            XmlSerializer serializer = new(ManifestFile.GetType());
            XmlSerializerNamespaces ns = new();
            ns.Add("", "");
            var entry = outputFile.CreateEntry(ManifestFileName);
            using var stream = entry.Open();
            serializer.Serialize(stream, ManifestFile, ns);
        }
        #endregion
    }
}
