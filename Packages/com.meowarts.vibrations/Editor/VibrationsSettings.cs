using UnityEditor;

namespace Vibrations
{
    [FilePath("ProjectSettings/VibrationsSettings.asset", FilePathAttribute.Location.ProjectFolder)]
    public class VibrationsSettings : ScriptableSingleton<VibrationsSettings>
    {
        public int frameRate = 30;
        public int defaultPreset;
        public string exportFolder = "Assets/Animations/Vibrations";
        public string templatesFolder = "Assets/Animations/Vibrations/Templates";

        public void SaveSettings() => Save(true);
    }
}
