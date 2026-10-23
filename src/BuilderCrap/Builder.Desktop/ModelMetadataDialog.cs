using Builder.Core;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Builder.Desktop;

/// <summary>Local form drafts only. The supplied Core action owns validation, revision and history.</summary>
public sealed class ModelMetadataDialog : Window
{
    private readonly ModelMetadata original;
    private readonly Func<ModelMetadata,OperationResult> apply;
    private readonly TextBox title,designer,description,copyright,license;
    private readonly TextBlock error;
    public bool Accepted {get;private set;}
    public OperationResult? LastResult {get;private set;}
    public ModelMetadataDialog(ModelMetadata metadata,Func<ModelMetadata,OperationResult> apply)
    {
        original=metadata;this.apply=apply;
        Title="About this model";Width=560;Height=640;MinWidth=460;MinHeight=480;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        SetResourceReference(StyleProperty,typeof(Window));
        NameScope.SetNameScope(this,new NameScope());
        var root=new Grid();root.SetResourceReference(Panel.BackgroundProperty,"ChromeFFFFFF");
        root.RowDefinitions.Add(new(){Height=GridLength.Auto});root.RowDefinitions.Add(new(){Height=new(1,GridUnitType.Star)});root.RowDefinitions.Add(new(){Height=GridLength.Auto});
        var heading=new StackPanel{Margin=new(20,18,20,12)};
        heading.Children.Add(new TextBlock{Text="About this model",FontSize=20,FontWeight=FontWeights.SemiBold});
        heading.Children.Add(new TextBlock{Text="3MF retains model information. STL contains geometry only. This title does not rename the file or its objects.",TextWrapping=TextWrapping.Wrap,FontSize=12,Margin=new(0,8,0,0)});root.Children.Add(heading);
        var fields=new StackPanel{Margin=new(20,0,20,16)};
        title=Field("ModelTitle","Title",metadata.Title,ModelMetadata.MaxTitleLength);designer=Field("ModelDesigner","Designer",metadata.Designer,ModelMetadata.MaxDesignerLength);
        description=Field("ModelDescription","Description",metadata.Description,ModelMetadata.MaxDescriptionLength,true,112);
        copyright=Field("ModelCopyright","Copyright",metadata.Copyright,ModelMetadata.MaxCopyrightLength);
        license=Field("ModelLicenseTerms","License terms",metadata.LicenseTerms,ModelMetadata.MaxLicenseTermsLength,true,88);
        var scroll=new ScrollViewer{Content=fields,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};RegisterName("ModelMetadataScroll",scroll);Grid.SetRow(scroll,1);root.Children.Add(scroll);
        var footer=new StackPanel{Margin=new(20,12,20,18)};
        error=new AccessibleStatusText{TextWrapping=TextWrapping.Wrap,Visibility=Visibility.Collapsed,Margin=new(0,0,0,10)};error.SetResourceReference(TextBlock.ForegroundProperty,"ErrorInk");RegisterName("ModelMetadataError",error);footer.Children.Add(error);
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};
        var accept=new Button{Content="_Apply",MinWidth=96};accept.SetResourceReference(StyleProperty,"PrimaryButton");RegisterName("ApplyModelMetadataButton",accept);
        var cancel=new Button{Content="_Cancel",MinWidth=96,IsCancel=true,Margin=new(0)};RegisterName("CancelModelMetadataButton",cancel);
        accept.Click+=(_,_)=>ApplyDraft();cancel.Click+=(_,_)=>Close();buttons.Children.Add(accept);buttons.Children.Add(cancel);footer.Children.Add(buttons);
        var footerBorder=new Border{BorderThickness=new(0,1,0,0),Child=footer};footerBorder.SetResourceReference(Border.BorderBrushProperty,"Line");Grid.SetRow(footerBorder,2);root.Children.Add(footerBorder);Content=root;
        ContentRendered+=(_,_)=>{title.Focus();title.SelectAll();};
        PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Escape){e.Handled=true;Close();}};
        TextBox Field(string name,string label,string value,int maximum,bool multiline=false,double height=34)
        {
            fields.Children.Add(new TextBlock{Text=label,Margin=new(0,12,0,5)});
            var box=new TextBox{Text=value,MaxLength=maximum,Height=height,AcceptsReturn=multiline,TextWrapping=multiline?TextWrapping.Wrap:TextWrapping.NoWrap,VerticalScrollBarVisibility=multiline?ScrollBarVisibility.Auto:ScrollBarVisibility.Hidden};
            RegisterName(name,box);System.Windows.Automation.AutomationProperties.SetName(box,"Model "+label.ToLowerInvariant());fields.Children.Add(box);return box;
        }
    }
    private void ApplyDraft()
    {
        // Clone rather than reconstruct: retained document language and future metadata stay intact.
        var draft=original with{Title=title.Text,Designer=designer.Text,Description=description.Text,Copyright=copyright.Text,LicenseTerms=license.Text};
        LastResult=apply(draft);
        if(LastResult.Succeeded){Accepted=true;Close();}
        else{error.Text=LastResult.Message;error.Visibility=Visibility.Visible;}
    }
}
