@rem Builds mb_Chromecast.dll with the C# compiler that ships with Windows.
@cd /d "%~dp0"
@"%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe" /nologo /target:library /optimize /out:mb_Chromecast.dll ^
  /r:System.Web.Extensions.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:Microsoft.VisualBasic.dll ^
  Chromecast.cs MusicBeeInterface.cs
