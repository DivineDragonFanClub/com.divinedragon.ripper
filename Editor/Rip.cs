using System;
using System.Xml.XPath;
using UnityEngine;

namespace DivineDragon
{
    public class Rip
    {
        public static bool RunAssetRipper(string assetRipperPath, string inputFile, string outputPath)
        {
            Debug.Log(outputPath);
            
            if (string.IsNullOrEmpty(assetRipperPath))
                throw new ArgumentNullException(assetRipperPath);
            
            if (string.IsNullOrEmpty(inputFile))
                throw new ArgumentNullException(inputFile);
            
            if (string.IsNullOrEmpty(outputPath))
                throw new ArgumentNullException(outputPath);
            
            using (AssetRipperRunner ripperRunner = new AssetRipperRunner(assetRipperPath))
            {
                Debug.Log($"AssetRipper running: {ripperRunner.Running}");
                
                ripperRunner.SetDefaultUnityVersion();
                
                ripperRunner.AddFile(inputFile);

                ripperRunner.ExportProject(outputPath);
                
                return true;
            }
        }
    }
}