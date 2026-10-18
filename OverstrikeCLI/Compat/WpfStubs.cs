// overstrike-cli -- Linux command-line port of Overstrike.
// Licensed under GPLv3, same as Overstrike. See LICENSE.txt.
//
// Stand-ins for the WPF / GUI-only pieces that the shared Overstrike installer code touches.
// The installers themselves never draw anything; these just let the code compile without WPF.

global using System;
global using System.Collections.Generic;

using Newtonsoft.Json.Linq;
using DAT1;
using DAT1.Files;
using Overstrike.Data;

namespace System.Windows.Media.Imaging {
	/// <summary>Placeholder for WPF's BitmapImage (used only for GUI banners/badges).</summary>
	public class BitmapImage {}
}

namespace Overstrike.Utils {
	internal static class Imaging {
		public static System.Windows.Media.Imaging.BitmapImage ConvertToBitmapImage(object _) => null;
	}
}

namespace Overstrike.Properties {
	/// <summary>Image resources exist only in the GUI build; the CLI never displays them.</summary>
	internal static class Resources {
		public static object badge_mmpc => null;
		public static object badge_modular => null;
		public static object badge_script => null;
		public static object badge_smpc => null;
		public static object badge_stage => null;
		public static object badge_style => null;
		public static object badge_suit => null;
		public static object badge_suit2 => null;
		public static object banner_i30_back => null;
		public static object banner_i30_logo => null;
		public static object banner_i30_logo2 => null;
		public static object banner_i33_logo => null;
		public static object banner_mm_back => null;
		public static object banner_mm_logo => null;
		public static object banner_mm_logo2 => null;
		public static object banner_msm2_logo => null;
		public static object banner_msm2_logo2 => null;
		public static object banner_msmr_back => null;
		public static object banner_msmr_logo => null;
		public static object banner_msmr_logo2 => null;
		public static object banner_rcra_back => null;
		public static object banner_rcra_logo => null;
		public static object banner_rcra_logo2 => null;
	}
}

namespace Overstrike {
	/// <summary>Minimal replacement for the WPF App object: only SuitsCache is used by installers.</summary>
	internal class App {
		public static App Current { get; } = new App();
		public SuitsCache SuitsCache { get; set; } = new SuitsCache();
	}
}

namespace Overstrike.Tabs {
	// Static config loaders copied from the GUI suits-menu tabs (Tabs/SuitsMenuBase.cs, Tabs/MSM2SuitsMenu.xaml.cs).
	internal static class SuitsMenuBase {
		public static JObject LoadConfig(TOC_I20 toc) {
			try {
				const ulong SYSTEM_PROGRESSION_CONFIG_AID = 0x9C9C72A303FCFA30; // configs/system/system_progression.config
				var config = new Config(toc.GetAssetReader(SYSTEM_PROGRESSION_CONFIG_AID));
				var root = config.ContentSection.Root;
				foreach (var techlist in root["TechWebLists"]) {
					if ((string)techlist["Description"] == "Suits") {
						return new JObject() {
							["suits"] = (JArray)techlist["TechWebItems"]
						};
					}
				}
			} catch {}

			return null;
		}
	}

	internal static class MSM2SuitsMenu {
		public static JObject LoadConfig_MSM2(TOC_I29 toc) {
			try {
				const ulong SYSTEM_PROGRESSION_CONFIG_AID = 0x9C9C72A303FCFA30;
				var config = new Config_I30(toc.GetAssetReader(SYSTEM_PROGRESSION_CONFIG_AID));
				var root = config.ContentSection.Data;
				return new JObject() { ["suits"] = root["SuitList"]["Suits"] };
			} catch {}

			return null;
		}
	}
}
