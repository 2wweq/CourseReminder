// WPF 与 WinForms 同时启用时，同名类型二义性用别名消除
global using Application = System.Windows.Application;
global using MessageBox = System.Windows.MessageBox;
global using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
