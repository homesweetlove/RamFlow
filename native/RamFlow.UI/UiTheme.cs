using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;

namespace RamFlow.UI;

internal static class UiTheme
{
    public static readonly SolidColorBrush Background = Brush("#101923");
    public static readonly SolidColorBrush Surface = Brush("#182535");
    public static readonly SolidColorBrush Elevated = Brush("#223247");
    public static readonly SolidColorBrush Border = Brush("#36495F");
    public static readonly SolidColorBrush Text = Brush("#EDF3FA");
    public static readonly SolidColorBrush Muted = Brush("#AEBDD0");
    public static readonly SolidColorBrush Accent = Brush("#77BEFF");
    public static readonly SolidColorBrush Good = Brush("#7CD7B4");
    public static readonly SolidColorBrush Warning = Brush("#F6CE83");
    public static readonly SolidColorBrush Danger = Brush("#FF969F");

    private static SolidColorBrush Brush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    public static void Install(Application app)
    {
        app.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse("""
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <SolidColorBrush x:Key="WindowBrush" Color="#101923"/>
              <SolidColorBrush x:Key="SurfaceBrush" Color="#182535"/>
              <SolidColorBrush x:Key="ElevatedBrush" Color="#223247"/>
              <SolidColorBrush x:Key="LineBrush" Color="#36495F"/>
              <SolidColorBrush x:Key="TextBrush" Color="#EDF3FA"/>
              <SolidColorBrush x:Key="MutedBrush" Color="#AEBDD0"/>
              <SolidColorBrush x:Key="AccentBrush" Color="#77BEFF"/>
              <Style TargetType="Window">
                <Setter Property="Background" Value="{StaticResource WindowBrush}"/>
                <Setter Property="Foreground" Value="{StaticResource TextBrush}"/>
                <Setter Property="FontFamily" Value="Malgun Gothic, Segoe UI"/>
                <Setter Property="FontSize" Value="13"/>
                <Setter Property="UseLayoutRounding" Value="True"/>
                <Setter Property="SnapsToDevicePixels" Value="True"/>
              </Style>
              <Style TargetType="TextBlock">
                <Setter Property="Foreground" Value="{StaticResource TextBrush}"/>
              </Style>
              <Style TargetType="Button">
                <Setter Property="Foreground" Value="{StaticResource TextBrush}"/>
                <Setter Property="Background" Value="{StaticResource ElevatedBrush}"/>
                <Setter Property="BorderBrush" Value="{StaticResource LineBrush}"/>
                <Setter Property="BorderThickness" Value="1"/>
                <Setter Property="Padding" Value="16,9"/>
                <Setter Property="Cursor" Value="Hand"/>
                <Setter Property="Template">
                  <Setter.Value>
                    <ControlTemplate TargetType="Button">
                      <Border x:Name="Frame" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                              BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="6" Padding="{TemplateBinding Padding}">
                        <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center" RecognizesAccessKey="True"/>
                      </Border>
                      <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Frame" Property="BorderBrush" Value="{StaticResource AccentBrush}"/></Trigger>
                        <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Frame" Property="BorderBrush" Value="{StaticResource AccentBrush}"/></Trigger>
                        <Trigger Property="IsPressed" Value="True"><Setter TargetName="Frame" Property="Opacity" Value="0.7"/></Trigger>
                        <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger>
                      </ControlTemplate.Triggers>
                    </ControlTemplate>
                  </Setter.Value>
                </Setter>
              </Style>
              <Style TargetType="TextBox">
                <Setter Property="Foreground" Value="{StaticResource TextBrush}"/>
                <Setter Property="Background" Value="{StaticResource WindowBrush}"/>
                <Setter Property="BorderBrush" Value="{StaticResource LineBrush}"/>
                <Setter Property="CaretBrush" Value="{StaticResource AccentBrush}"/>
                <Setter Property="SelectionBrush" Value="#43688F"/>
                <Setter Property="Padding" Value="10,8"/>
                <Style.Triggers>
                  <Trigger Property="IsKeyboardFocused" Value="True"><Setter Property="BorderBrush" Value="{StaticResource AccentBrush}"/></Trigger>
                </Style.Triggers>
              </Style>
              <Style TargetType="CheckBox">
                <Setter Property="Foreground" Value="{StaticResource TextBrush}"/>
                <Setter Property="Margin" Value="0,7"/>
                <Setter Property="VerticalContentAlignment" Value="Center"/>
                <Setter Property="Cursor" Value="Hand"/>
              </Style>
              <Style TargetType="ComboBox">
                <Setter Property="Foreground" Value="{StaticResource TextBrush}"/>
                <Setter Property="Background" Value="{StaticResource ElevatedBrush}"/>
                <Setter Property="BorderBrush" Value="{StaticResource LineBrush}"/>
                <Setter Property="Padding" Value="10,8"/>
                <Setter Property="ScrollViewer.HorizontalScrollBarVisibility" Value="Disabled"/>
                <Setter Property="ScrollViewer.VerticalScrollBarVisibility" Value="Auto"/>
                <Setter Property="Template">
                  <Setter.Value>
                    <ControlTemplate TargetType="ComboBox">
                      <Grid>
                        <Border x:Name="Frame" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="1" CornerRadius="5"/>
                        <ToggleButton Focusable="False" IsChecked="{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}" ClickMode="Press">
                          <ToggleButton.Template><ControlTemplate TargetType="ToggleButton"><Border Background="Transparent"><TextBlock Text="▾" Foreground="{StaticResource MutedBrush}" HorizontalAlignment="Right" VerticalAlignment="Center" Margin="0,0,12,0"/></Border></ControlTemplate></ToggleButton.Template>
                        </ToggleButton>
                        <ContentPresenter Margin="10,8,32,8" IsHitTestVisible="False" VerticalAlignment="Center" Content="{TemplateBinding SelectionBoxItem}"
                                          ContentTemplate="{TemplateBinding SelectionBoxItemTemplate}"/>
                        <Popup x:Name="PART_Popup" Placement="Bottom" IsOpen="{TemplateBinding IsDropDownOpen}" AllowsTransparency="True" Focusable="False" PopupAnimation="Fade">
                          <Border Background="{StaticResource SurfaceBrush}" BorderBrush="{StaticResource LineBrush}" BorderThickness="1" MinWidth="{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}" MaxHeight="320" Padding="4">
                            <ScrollViewer CanContentScroll="True"><ItemsPresenter KeyboardNavigation.DirectionalNavigation="Contained"/></ScrollViewer>
                          </Border>
                        </Popup>
                      </Grid>
                      <ControlTemplate.Triggers>
                        <Trigger Property="IsKeyboardFocusWithin" Value="True"><Setter TargetName="Frame" Property="BorderBrush" Value="{StaticResource AccentBrush}"/></Trigger>
                        <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger>
                      </ControlTemplate.Triggers>
                    </ControlTemplate>
                  </Setter.Value>
                </Setter>
              </Style>
              <Style TargetType="ComboBoxItem">
                <Setter Property="Foreground" Value="{StaticResource TextBrush}"/>
                <Setter Property="Padding" Value="10,7"/>
                <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ComboBoxItem">
                  <Border x:Name="Frame" Background="Transparent" Padding="{TemplateBinding Padding}" CornerRadius="3"><ContentPresenter/></Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsHighlighted" Value="True"><Setter TargetName="Frame" Property="Background" Value="{StaticResource ElevatedBrush}"/></Trigger>
                    <Trigger Property="IsSelected" Value="True"><Setter TargetName="Frame" Property="Background" Value="#294B6C"/></Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate></Setter.Value></Setter>
              </Style>
              <Style TargetType="TabControl">
                <Setter Property="Background" Value="{StaticResource WindowBrush}"/>
                <Setter Property="BorderThickness" Value="0"/>
                <Setter Property="Padding" Value="0,18,0,0"/>
              </Style>
              <Style TargetType="TabItem">
                <Setter Property="Foreground" Value="{StaticResource MutedBrush}"/>
                <Setter Property="Padding" Value="16,11"/>
                <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="TabItem">
                  <Border x:Name="Frame" Padding="{TemplateBinding Padding}" Background="Transparent" BorderThickness="0,0,0,2" BorderBrush="Transparent">
                    <ContentPresenter ContentSource="Header" RecognizesAccessKey="True"/>
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsSelected" Value="True"><Setter Property="Foreground" Value="{StaticResource TextBrush}"/><Setter TargetName="Frame" Property="BorderBrush" Value="{StaticResource AccentBrush}"/><Setter TargetName="Frame" Property="Background" Value="{StaticResource SurfaceBrush}"/></Trigger>
                    <Trigger Property="IsMouseOver" Value="True"><Setter Property="Foreground" Value="{StaticResource TextBrush}"/></Trigger>
                    <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Frame" Property="Background" Value="{StaticResource ElevatedBrush}"/></Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate></Setter.Value></Setter>
              </Style>
              <Style TargetType="DataGrid">
                <Setter Property="Background" Value="{StaticResource SurfaceBrush}"/>
                <Setter Property="Foreground" Value="{StaticResource TextBrush}"/>
                <Setter Property="BorderBrush" Value="{StaticResource LineBrush}"/>
                <Setter Property="RowBackground" Value="{StaticResource SurfaceBrush}"/>
                <Setter Property="AlternatingRowBackground" Value="#1C2B3C"/>
                <Setter Property="GridLinesVisibility" Value="None"/>
                <Setter Property="HeadersVisibility" Value="Column"/>
                <Setter Property="RowHeaderWidth" Value="0"/>
                <Setter Property="RowHeight" Value="37"/>
                <Setter Property="CanUserAddRows" Value="False"/>
                <Setter Property="CanUserDeleteRows" Value="False"/>
                <Setter Property="AutoGenerateColumns" Value="False"/>
                <Setter Property="IsReadOnly" Value="True"/>
                <Setter Property="EnableRowVirtualization" Value="True"/>
                <Setter Property="EnableColumnVirtualization" Value="True"/>
              </Style>
              <Style TargetType="DataGridColumnHeader">
                <Setter Property="Background" Value="{StaticResource ElevatedBrush}"/>
                <Setter Property="Foreground" Value="{StaticResource MutedBrush}"/>
                <Setter Property="Padding" Value="10,11"/>
                <Setter Property="BorderThickness" Value="0"/>
              </Style>
              <Style TargetType="DataGridCell">
                <Setter Property="Padding" Value="10,7"/>
                <Setter Property="BorderThickness" Value="0"/>
                <Style.Triggers>
                  <Trigger Property="IsSelected" Value="True"><Setter Property="Background" Value="#294B6C"/><Setter Property="Foreground" Value="{StaticResource TextBrush}"/></Trigger>
                </Style.Triggers>
              </Style>
              <Style TargetType="ProgressBar">
                <Setter Property="Background" Value="{StaticResource ElevatedBrush}"/>
                <Setter Property="Foreground" Value="{StaticResource AccentBrush}"/>
                <Setter Property="BorderThickness" Value="0"/>
                <Setter Property="Height" Value="6"/>
              </Style>
            </ResourceDictionary>
            """));
    }
}
