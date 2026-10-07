using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;

namespace RamFlow.UI;

internal static class UiTheme
{
    public static readonly SolidColorBrush Background = Brush("#0B1220");
    public static readonly SolidColorBrush Surface = Brush("#121E2E");
    public static readonly SolidColorBrush Elevated = Brush("#1C2D42");
    public static readonly SolidColorBrush Border = Brush("#30475E");
    public static readonly SolidColorBrush Text = Brush("#EDF5FB");
    public static readonly SolidColorBrush Muted = Brush("#A3B7C9");
    public static readonly SolidColorBrush Accent = Brush("#68D9EC");
    public static readonly SolidColorBrush Good = Brush("#6BD4B5");
    public static readonly SolidColorBrush Warning = Brush("#EEC582");
    public static readonly SolidColorBrush Danger = Brush("#F49DA9");

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
              <SolidColorBrush x:Key="WindowBrush" Color="#0B1220"/>
              <SolidColorBrush x:Key="SurfaceBrush" Color="#121E2E"/>
              <SolidColorBrush x:Key="ElevatedBrush" Color="#1C2D42"/>
              <SolidColorBrush x:Key="LineBrush" Color="#30475E"/>
              <SolidColorBrush x:Key="TextBrush" Color="#EDF5FB"/>
              <SolidColorBrush x:Key="MutedBrush" Color="#A3B7C9"/>
              <SolidColorBrush x:Key="AccentBrush" Color="#68D9EC"/>
              <SolidColorBrush x:Key="HoverBrush" Color="#21394E"/>
              <SolidColorBrush x:Key="SelectedBrush" Color="#234B5E"/>
              <SolidColorBrush x:Key="InactiveSelectedBrush" Color="#253748"/>
              <SolidColorBrush x:Key="DangerBrush" Color="#F49DA9"/>
              <SolidColorBrush x:Key="{x:Static SystemColors.ControlBrushKey}" Color="#121E2E"/>
              <SolidColorBrush x:Key="{x:Static SystemColors.WindowBrushKey}" Color="#0B1220"/>
              <SolidColorBrush x:Key="{x:Static SystemColors.ControlTextBrushKey}" Color="#EDF5FB"/>
              <SolidColorBrush x:Key="{x:Static SystemColors.WindowTextBrushKey}" Color="#EDF5FB"/>
              <SolidColorBrush x:Key="{x:Static SystemColors.HighlightBrushKey}" Color="#234B5E"/>
              <SolidColorBrush x:Key="{x:Static SystemColors.HighlightTextBrushKey}" Color="#EDF5FB"/>
              <SolidColorBrush x:Key="{x:Static SystemColors.InactiveSelectionHighlightBrushKey}" Color="#253748"/>
              <SolidColorBrush x:Key="{x:Static SystemColors.InactiveSelectionHighlightTextBrushKey}" Color="#EDF5FB"/>
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
                <Setter Property="Padding" Value="15,9"/>
                <Setter Property="MinHeight" Value="36"/>
                <Setter Property="Cursor" Value="Hand"/>
                <Setter Property="Template">
                  <Setter.Value>
                    <ControlTemplate TargetType="Button">
                      <Border x:Name="Frame" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                              BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="7" Padding="{TemplateBinding Padding}">
                        <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center" RecognizesAccessKey="True"/>
                      </Border>
                      <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Frame" Property="Background" Value="{StaticResource HoverBrush}"/><Setter TargetName="Frame" Property="BorderBrush" Value="{StaticResource AccentBrush}"/></Trigger>
                        <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Frame" Property="BorderBrush" Value="{StaticResource AccentBrush}"/></Trigger>
                        <Trigger Property="IsPressed" Value="True"><Setter TargetName="Frame" Property="Background" Value="{StaticResource SelectedBrush}"/></Trigger>
                        <Trigger Property="IsEnabled" Value="False"><Setter Property="Foreground" Value="{StaticResource MutedBrush}"/><Setter TargetName="Frame" Property="Background" Value="{StaticResource SurfaceBrush}"/><Setter Property="Opacity" Value="0.65"/></Trigger>
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
                <Setter Property="SelectionBrush" Value="{StaticResource SelectedBrush}"/>
                <Setter Property="SelectionOpacity" Value="1"/>
                <Setter Property="SelectionTextBrush" Value="{StaticResource TextBrush}"/>
                <Setter Property="Padding" Value="10,8"/>
                <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="TextBox">
                  <Border x:Name="Frame" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                          BorderThickness="1" CornerRadius="6" Padding="{TemplateBinding Padding}">
                    <ScrollViewer x:Name="PART_ContentHost" Focusable="False"/>
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Frame" Property="BorderBrush" Value="#54728A"/></Trigger>
                    <Trigger Property="IsKeyboardFocusWithin" Value="True"><Setter TargetName="Frame" Property="BorderBrush" Value="{StaticResource AccentBrush}"/></Trigger>
                    <Trigger Property="Validation.HasError" Value="True"><Setter TargetName="Frame" Property="BorderBrush" Value="{StaticResource DangerBrush}"/></Trigger>
                    <Trigger Property="IsEnabled" Value="False"><Setter Property="Foreground" Value="{StaticResource MutedBrush}"/><Setter Property="Opacity" Value="0.65"/></Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate></Setter.Value></Setter>
              </Style>
              <Style TargetType="PasswordBox">
                <Setter Property="Foreground" Value="{StaticResource TextBrush}"/>
                <Setter Property="Background" Value="{StaticResource WindowBrush}"/>
                <Setter Property="BorderBrush" Value="{StaticResource LineBrush}"/>
                <Setter Property="CaretBrush" Value="{StaticResource AccentBrush}"/>
                <Setter Property="SelectionBrush" Value="{StaticResource SelectedBrush}"/>
                <Setter Property="Padding" Value="10,8"/>
                <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="PasswordBox">
                  <Border x:Name="Frame" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                          BorderThickness="1" CornerRadius="6" Padding="{TemplateBinding Padding}">
                    <ScrollViewer x:Name="PART_ContentHost" Focusable="False"/>
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Frame" Property="BorderBrush" Value="#54728A"/></Trigger>
                    <Trigger Property="IsKeyboardFocusWithin" Value="True"><Setter TargetName="Frame" Property="BorderBrush" Value="{StaticResource AccentBrush}"/></Trigger>
                    <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.65"/></Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate></Setter.Value></Setter>
              </Style>
              <Style TargetType="CheckBox">
                <Setter Property="Foreground" Value="{StaticResource TextBrush}"/>
                <Setter Property="Margin" Value="0,7"/>
                <Setter Property="VerticalContentAlignment" Value="Center"/>
                <Setter Property="Cursor" Value="Hand"/>
                <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="CheckBox">
                  <Border x:Name="Focus" BorderBrush="Transparent" BorderThickness="1" CornerRadius="5" Padding="3">
                    <Grid>
                      <Grid.ColumnDefinitions><ColumnDefinition Width="20"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                      <Border x:Name="Box" Width="18" Height="18" VerticalAlignment="Center" Background="{StaticResource ElevatedBrush}"
                              BorderBrush="{StaticResource LineBrush}" BorderThickness="1" CornerRadius="4">
                        <Grid>
                          <Path x:Name="Tick" Data="M 3,8 L 7,12 L 14,4" Stroke="{StaticResource WindowBrush}" StrokeThickness="2"
                                StrokeStartLineCap="Round" StrokeEndLineCap="Round" Visibility="Collapsed"/>
                          <Border x:Name="Dash" Width="8" Height="2" Background="{StaticResource WindowBrush}" Visibility="Collapsed"/>
                        </Grid>
                      </Border>
                      <ContentPresenter Grid.Column="1" Margin="10,0,0,0" VerticalAlignment="{TemplateBinding VerticalContentAlignment}" RecognizesAccessKey="True"/>
                    </Grid>
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Box" Property="BorderBrush" Value="{StaticResource AccentBrush}"/></Trigger>
                    <Trigger Property="IsChecked" Value="True"><Setter TargetName="Box" Property="Background" Value="{StaticResource AccentBrush}"/><Setter TargetName="Box" Property="BorderBrush" Value="{StaticResource AccentBrush}"/><Setter TargetName="Tick" Property="Visibility" Value="Visible"/></Trigger>
                    <Trigger Property="IsChecked" Value="{x:Null}"><Setter TargetName="Box" Property="Background" Value="{StaticResource AccentBrush}"/><Setter TargetName="Dash" Property="Visibility" Value="Visible"/></Trigger>
                    <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Focus" Property="BorderBrush" Value="{StaticResource AccentBrush}"/></Trigger>
                    <Trigger Property="IsEnabled" Value="False"><Setter Property="Foreground" Value="{StaticResource MutedBrush}"/><Setter Property="Opacity" Value="0.65"/></Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate></Setter.Value></Setter>
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
                        <Border x:Name="Frame" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="1" CornerRadius="6"/>
                        <ToggleButton Focusable="False" IsChecked="{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}" ClickMode="Press">
                          <ToggleButton.Template><ControlTemplate TargetType="ToggleButton"><Border Background="Transparent"><TextBlock Text="▾" Foreground="{StaticResource MutedBrush}" HorizontalAlignment="Right" VerticalAlignment="Center" Margin="0,0,12,0"/></Border></ControlTemplate></ToggleButton.Template>
                        </ToggleButton>
                        <ContentPresenter Margin="10,8,32,8" IsHitTestVisible="False" VerticalAlignment="Center" Content="{TemplateBinding SelectionBoxItem}"
                                          ContentTemplate="{TemplateBinding SelectionBoxItemTemplate}"/>
                        <Popup x:Name="PART_Popup" Placement="Bottom" IsOpen="{TemplateBinding IsDropDownOpen}" AllowsTransparency="True" Focusable="False" PopupAnimation="Fade">
                          <Border Background="{StaticResource SurfaceBrush}" BorderBrush="{StaticResource LineBrush}" BorderThickness="1" CornerRadius="6" MinWidth="{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}" MaxHeight="320" Padding="4">
                            <ScrollViewer CanContentScroll="True"><ItemsPresenter KeyboardNavigation.DirectionalNavigation="Contained"/></ScrollViewer>
                          </Border>
                        </Popup>
                      </Grid>
                      <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Frame" Property="BorderBrush" Value="#54728A"/></Trigger>
                        <Trigger Property="IsDropDownOpen" Value="True"><Setter TargetName="Frame" Property="BorderBrush" Value="{StaticResource AccentBrush}"/></Trigger>
                        <Trigger Property="IsKeyboardFocusWithin" Value="True"><Setter TargetName="Frame" Property="BorderBrush" Value="{StaticResource AccentBrush}"/></Trigger>
                        <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.65"/></Trigger>
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
                    <Trigger Property="IsHighlighted" Value="True"><Setter TargetName="Frame" Property="Background" Value="{StaticResource HoverBrush}"/></Trigger>
                    <Trigger Property="IsSelected" Value="True"><Setter TargetName="Frame" Property="Background" Value="{StaticResource SelectedBrush}"/></Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate></Setter.Value></Setter>
              </Style>
              <Style TargetType="TabControl">
                <Setter Property="Background" Value="{StaticResource WindowBrush}"/>
                <Setter Property="BorderThickness" Value="0"/>
                <Setter Property="Padding" Value="0,14,0,0"/>
              </Style>
              <Style TargetType="TabItem">
                <Setter Property="Foreground" Value="{StaticResource MutedBrush}"/>
                <Setter Property="Padding" Value="16,11"/>
                <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="TabItem">
                  <Border x:Name="Frame" Padding="{TemplateBinding Padding}" Background="Transparent" BorderThickness="0,0,0,2" BorderBrush="Transparent">
                    <ContentPresenter x:Name="Header" ContentSource="Header" RecognizesAccessKey="True"/>
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True"><Setter Property="Foreground" Value="{StaticResource TextBrush}"/><Setter TargetName="Frame" Property="Background" Value="{StaticResource ElevatedBrush}"/></Trigger>
                    <Trigger Property="IsSelected" Value="True"><Setter Property="Foreground" Value="{StaticResource TextBrush}"/><Setter TargetName="Header" Property="TextElement.FontWeight" Value="SemiBold"/><Setter TargetName="Frame" Property="BorderBrush" Value="{StaticResource AccentBrush}"/><Setter TargetName="Frame" Property="Background" Value="{StaticResource SurfaceBrush}"/></Trigger>
                    <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Frame" Property="Background" Value="{StaticResource ElevatedBrush}"/></Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate></Setter.Value></Setter>
              </Style>
              <Style TargetType="DataGrid">
                <Setter Property="Background" Value="{StaticResource SurfaceBrush}"/>
                <Setter Property="Foreground" Value="{StaticResource TextBrush}"/>
                <Setter Property="BorderBrush" Value="{StaticResource LineBrush}"/>
                <Setter Property="RowBackground" Value="{StaticResource SurfaceBrush}"/>
                <Setter Property="AlternatingRowBackground" Value="#162638"/>
                <Setter Property="GridLinesVisibility" Value="None"/>
                <Setter Property="HeadersVisibility" Value="Column"/>
                <Setter Property="RowHeaderWidth" Value="0"/>
                <Setter Property="RowHeight" Value="37"/>
                <Setter Property="CanUserAddRows" Value="False"/>
                <Setter Property="CanUserDeleteRows" Value="False"/>
                <Setter Property="AutoGenerateColumns" Value="False"/>
                <Setter Property="IsReadOnly" Value="True"/>
                <Setter Property="ScrollViewer.CanContentScroll" Value="True"/>
                <Setter Property="VirtualizingPanel.IsVirtualizing" Value="True"/>
                <Setter Property="VirtualizingPanel.VirtualizationMode" Value="Recycling"/>
                <Setter Property="EnableRowVirtualization" Value="True"/>
                <Setter Property="EnableColumnVirtualization" Value="True"/>
              </Style>
              <Style TargetType="DataGridColumnHeader">
                <Setter Property="Background" Value="{StaticResource ElevatedBrush}"/>
                <Setter Property="Foreground" Value="{StaticResource MutedBrush}"/>
                <Setter Property="Padding" Value="10,11"/>
                <Setter Property="BorderThickness" Value="0"/>
                <Setter Property="FontWeight" Value="SemiBold"/>
              </Style>
              <Style TargetType="DataGridRow">
                <Setter Property="Foreground" Value="{StaticResource TextBrush}"/>
                <Style.Triggers>
                  <Trigger Property="IsMouseOver" Value="True"><Setter Property="Background" Value="{StaticResource HoverBrush}"/></Trigger>
                  <Trigger Property="IsSelected" Value="True"><Setter Property="Background" Value="{StaticResource SelectedBrush}"/></Trigger>
                  <MultiTrigger><MultiTrigger.Conditions><Condition Property="IsSelected" Value="True"/><Condition Property="Selector.IsSelectionActive" Value="False"/></MultiTrigger.Conditions>
                    <Setter Property="Background" Value="{StaticResource InactiveSelectedBrush}"/>
                  </MultiTrigger>
                </Style.Triggers>
              </Style>
              <Style TargetType="DataGridCell">
                <Setter Property="Padding" Value="10,7"/>
                <Setter Property="Foreground" Value="{StaticResource TextBrush}"/>
                <Setter Property="BorderBrush" Value="Transparent"/>
                <Setter Property="BorderThickness" Value="1"/>
                <Style.Triggers>
                  <Trigger Property="IsSelected" Value="True"><Setter Property="Background" Value="{StaticResource SelectedBrush}"/><Setter Property="Foreground" Value="{StaticResource TextBrush}"/></Trigger>
                  <MultiTrigger><MultiTrigger.Conditions><Condition Property="IsSelected" Value="True"/><Condition Property="Selector.IsSelectionActive" Value="False"/></MultiTrigger.Conditions>
                    <Setter Property="Background" Value="{StaticResource InactiveSelectedBrush}"/>
                  </MultiTrigger>
                  <Trigger Property="IsKeyboardFocusWithin" Value="True"><Setter Property="BorderBrush" Value="{StaticResource AccentBrush}"/></Trigger>
                </Style.Triggers>
              </Style>
              <ControlTemplate x:Key="ScrollPageButton" TargetType="RepeatButton">
                <Border Background="Transparent"/>
              </ControlTemplate>
              <Style x:Key="ScrollThumb" TargetType="Thumb">
                <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Thumb">
                  <Border x:Name="Grip" Background="{StaticResource LineBrush}" CornerRadius="4" Margin="3"/>
                  <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Grip" Property="Background" Value="{StaticResource MutedBrush}"/></Trigger>
                    <Trigger Property="IsDragging" Value="True"><Setter TargetName="Grip" Property="Background" Value="{StaticResource AccentBrush}"/></Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate></Setter.Value></Setter>
              </Style>
              <Style TargetType="ScrollBar">
                <Setter Property="Background" Value="{StaticResource WindowBrush}"/>
                <Setter Property="Width" Value="13"/>
                <Setter Property="MinWidth" Value="13"/>
                <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ScrollBar">
                  <Border Background="{TemplateBinding Background}">
                    <Track x:Name="PART_Track" Orientation="{TemplateBinding Orientation}" IsDirectionReversed="True"
                           Minimum="{TemplateBinding Minimum}" Maximum="{TemplateBinding Maximum}" Value="{Binding Value, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}" ViewportSize="{TemplateBinding ViewportSize}">
                      <Track.DecreaseRepeatButton><RepeatButton x:Name="Decrease" Template="{StaticResource ScrollPageButton}" Command="{x:Static ScrollBar.PageUpCommand}" CommandTarget="{Binding RelativeSource={RelativeSource TemplatedParent}}" Focusable="False"/></Track.DecreaseRepeatButton>
                      <Track.Thumb><Thumb Style="{StaticResource ScrollThumb}" MinWidth="13" MinHeight="13"/></Track.Thumb>
                      <Track.IncreaseRepeatButton><RepeatButton x:Name="Increase" Template="{StaticResource ScrollPageButton}" Command="{x:Static ScrollBar.PageDownCommand}" CommandTarget="{Binding RelativeSource={RelativeSource TemplatedParent}}" Focusable="False"/></Track.IncreaseRepeatButton>
                    </Track>
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property="Orientation" Value="Horizontal">
                      <Setter TargetName="PART_Track" Property="IsDirectionReversed" Value="False"/>
                      <Setter TargetName="Decrease" Property="Command" Value="{x:Static ScrollBar.PageLeftCommand}"/>
                      <Setter TargetName="Increase" Property="Command" Value="{x:Static ScrollBar.PageRightCommand}"/>
                    </Trigger>
                    <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate></Setter.Value></Setter>
                <Style.Triggers>
                  <Trigger Property="Orientation" Value="Horizontal"><Setter Property="Width" Value="Auto"/><Setter Property="MinWidth" Value="0"/><Setter Property="Height" Value="13"/></Trigger>
                </Style.Triggers>
              </Style>
              <Style TargetType="ToolTip">
                <Setter Property="Background" Value="{StaticResource ElevatedBrush}"/>
                <Setter Property="Foreground" Value="{StaticResource TextBrush}"/>
                <Setter Property="BorderBrush" Value="{StaticResource LineBrush}"/>
                <Setter Property="Padding" Value="10,7"/>
                <Setter Property="MaxWidth" Value="440"/>
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
