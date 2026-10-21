using System.Runtime.InteropServices;
using System.Windows;

[assembly:ThemeInfo(
    ResourceDictionaryLocation.None,            //where theme specific resource dictionaries are located
                                                //(used if a resource is not found in the page,
                                                // or application resource dictionaries)
    ResourceDictionaryLocation.SourceAssembly   //where the generic resource dictionary is located
                                                //(used if a resource is not found in the page,
                                                // app, or any theme specific resource dictionaries)
)]

// Fixed identity for WhisperDesk by darkcipher0x. Never change this GUID.
[assembly: Guid("d06d3e32-6317-4cc3-a20f-2645b1c78f11")]

// The security tests exercise internal pieces (sandbox launcher, file format internals).
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("WhisperDesk.Tests")]
