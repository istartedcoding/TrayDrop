# Third-party notices

TrayDrop's original source and documentation are provided under the [MIT license](https://github.com/istartedcoding/TrayDrop/blob/main/LICENSE). Third-party components retain their own copyright notices and license terms.

## Runtime components

| Component | Version in the 0.1.1 Windows release | License and notices |
| --- | --- | --- |
| QRCoder | 1.8.1 | MIT; original copyright and license text in `licenses\QRCoder-LICENSE.txt` |
| Microsoft.NETCore.App.Runtime.win-x64 | 10.0.14 | MIT and the runtime package's third-party notices |
| Microsoft.AspNetCore.App.Runtime.win-x64 | 10.0.14 | MIT and the runtime package's third-party notices |
| Microsoft.WindowsDesktop.App.Runtime.win-x64 | 10.0.14 | MIT; original license text from the runtime package |

The portable ZIP includes QRCoder's license and the Microsoft runtime packages' license files, with third-party notices where those packages provide them, inside its `licenses` folder. Preserve these files when redistributing the application.

Upstream sources:

- [QRCoder](https://github.com/Shane32/QRCoder), copyright Raffael Herrmann and Shane Krueger.
- [.NET runtime](https://github.com/dotnet/runtime), copyright .NET Foundation and contributors.
- [ASP.NET Core](https://github.com/dotnet/aspnetcore), copyright .NET Foundation and contributors.
- [Windows Forms](https://github.com/dotnet/winforms) and [WPF](https://github.com/dotnet/wpf), copyright .NET Foundation and contributors.

## Development dependencies

MSTest 4.0.2 is used by the test project. Test runners, test results, development dependencies, and debugging-symbol files are not part of the portable application package.

This file summarizes the included components. The original license and notice texts supplied with them govern their respective use and redistribution.
