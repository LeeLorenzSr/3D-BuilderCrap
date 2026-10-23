using Builder.Core;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Builder.Desktop;

public sealed class UnitsDialog : Window
{
    private readonly ComboBox units = new() { ItemsSource = new[] { "Millimeters (mm)","Centimeters (cm)","Inches (in)","Meters (m)" }, SelectedIndex = 0, Margin = new(0,6,0,22) };
    private readonly CheckBox geometryOnly = new() { Content="Geometry only — ignore nonstandard STL colors",Margin=new(0,0,0,10) };
    public ModelUnit Unit => (ModelUnit)units.SelectedIndex;
    public StlImportMode StlMode=>geometryOnly.IsChecked==true?StlImportMode.GeometryOnly:StlImportMode.Strict;
    public static ModelUnit DefaultUnit(string path)=>System.IO.Path.GetExtension(path).ToLowerInvariant() is ".gltf" or ".glb"?ModelUnit.Meters:ModelUnit.Millimeters;
    public UnitsDialog(string name,ModelUnit? initialUnit=null,bool exporting=false,bool insert=false,StlImportMode stlMode=StlImportMode.Strict)
    {
        SetResourceReference(StyleProperty,typeof(Window));
        units.SelectedIndex=(int)(initialUnit??DefaultUnit(name));
        var format=System.IO.Path.GetExtension(name).ToLowerInvariant() switch{".obj"=>"OBJ",".ply"=>"PLY",".gltf"=>"glTF",".glb"=>"GLB",_=>"STL"};
        Title=format+(exporting?" export units":insert?" import units":" units"); Width=440; SizeToContent=SizeToContent.Height; ResizeMode=ResizeMode.NoResize; WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var panel=new StackPanel { Margin=new(24) };
        panel.Children.Add(new TextBlock { Text=exporting?"Choose export units":"Choose the model’s units",FontSize=20,FontWeight=FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text=name,TextWrapping=TextWrapping.Wrap,Margin=new(0,12,0,12),FontWeight=FontWeights.SemiBold });
        var guidance=format switch
        {
            "glTF" or "GLB"=>exporting
                ?"glTF uses meters and Y-up. This choice controls coordinate values; the file retains its physical size. Objects and colors are kept. Use 3MF for model information."
                :"glTF uses meters and Y-up. Meters is recommended; override only for a nonstandard source. Opaque colors become flat face colors. Textures and animations are not supported.",
            "PLY"=>exporting
                ?"PLY coordinates use these units. Display colors are retained, but objects are combined into one mesh. Use 3MF to keep names, groups and model information."
                :"PLY files do not store units. The model will be converted to millimeters. Pieces open as one object; vertex colors become flat face colors.",
            _=>exporting
                ?"OBJ coordinates use these units. Keep the companion MTL file with the OBJ to retain colors. Use 3MF to keep model information."
                :format+" files do not store units. The model will be converted to millimeters when opened."
        };
        panel.Children.Add(new TextBlock { Text=guidance,TextWrapping=TextWrapping.Wrap,Margin=new(0,0,0,16) });
        panel.Children.Add(new TextBlock { Text=exporting?"Export units":"Source units" });
        AutomationProperties.SetName(units,format+(exporting?" export units":" source units"));
        panel.Children.Add(units);
        if(format=="STL"&&!exporting)
        {
            geometryOnly.IsChecked=stlMode==StlImportMode.GeometryOnly;
            AutomationProperties.SetName(geometryOnly,"Import STL geometry only and ignore nonstandard colors");
            panel.Children.Add(geometryOnly);
            panel.Children.Add(new TextBlock{Text="Some STL files contain nonstandard color attributes. Choose Geometry only to open their shapes without those colors. Leave it off to refuse unsupported attributes. Use 3MF when colors must be retained.",TextWrapping=TextWrapping.Wrap,FontSize=12,Margin=new(0,0,0,18)});
        }
        var buttons=new StackPanel { Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right };
        var cancel=new Button { Content="Cancel",IsCancel=true }; var open=new Button { Content=exporting?"Export model":insert?"Insert model":"Open model",IsDefault=true,Margin=new(0) };
        open.SetResourceReference(StyleProperty,"PrimaryButton");
        open.Click+=(_,_)=>DialogResult=true;
        buttons.Children.Add(cancel); buttons.Children.Add(open); panel.Children.Add(buttons);var border=new Border{Child=panel};border.SetResourceReference(Border.BackgroundProperty,"ChromeFFFFFF");Content=border;
        ContentRendered+=(_,_)=>units.Focus();
    }
}
