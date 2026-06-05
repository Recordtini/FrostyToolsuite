using Frosty.Core.Attributes;
using System.Runtime.InteropServices;

[assembly: ComVisible(false)]
[assembly: Guid("60d94e9c-149f-4f40-83c8-49db04f5af63")]

[assembly: PluginDisplayName("College Football 27 Profile")]
[assembly: PluginAuthor("CFBFrosty")]
[assembly: PluginVersion("1.0.0.0")]
[assembly: RegisterProfile(typeof(CFBProfilePlugin.CollegeFootball27Profile))]
[assembly: RegisterProfile(typeof(CFBProfilePlugin.CollegeFootball27TrialProfile))]
