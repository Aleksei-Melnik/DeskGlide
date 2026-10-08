namespace SdrCapture.Ui;
internal sealed record StatusSection(string Title,params (string Name,string Value)[] Values);
