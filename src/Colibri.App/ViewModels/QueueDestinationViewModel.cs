using CommunityToolkit.Mvvm.Input;
namespace Colibri.App.ViewModels;
public sealed record QueueDestinationViewModel(string Name, IAsyncRelayCommand Command);
