using System.ComponentModel;
using System.Runtime.CompilerServices;
using Ghostty.Core;
using Xunit;

namespace Ghostty.Tests;

public class AotBindingTests
{
    [Fact]
    public void Create_RunsUpdateImmediately()
    {
        var model = new FakeModel { Title = "hello" };
        string? captured = null;

        AotBinding.Create(model, item => captured = ((FakeModel)item).Title);

        Assert.Equal("hello", captured);
    }

    [Fact]
    public void PropertyChange_FiresUpdate_WhenWatched()
    {
        var model = new FakeModel { Title = "initial" };
        int callCount = 0;
        string? lastTitle = null;

        AotBinding.Create(model, item =>
        {
            callCount++;
            lastTitle = ((FakeModel)item).Title;
        }, nameof(FakeModel.Title));

        Assert.Equal(1, callCount);

        model.Title = "updated";
        Assert.Equal(2, callCount);
        Assert.Equal("updated", lastTitle);
    }

    [Fact]
    public void PropertyChange_SkipsUpdate_WhenNotWatched()
    {
        var model = new FakeModel { Title = "a", Count = 0 };
        int callCount = 0;

        AotBinding.Create(model, _ => callCount++, nameof(FakeModel.Title));

        Assert.Equal(1, callCount);

        model.Count = 42;
        Assert.Equal(1, callCount);
    }

    [Fact]
    public void PropertyChange_FiresForAll_WhenNoFilter()
    {
        var model = new FakeModel { Title = "a", Count = 0 };
        int callCount = 0;

        AotBinding.Create(model, _ => callCount++);

        Assert.Equal(1, callCount);

        model.Title = "b";
        Assert.Equal(2, callCount);

        model.Count = 5;
        Assert.Equal(3, callCount);
    }

    [Fact]
    public void Dispose_DetachesSubscription()
    {
        var model = new FakeModel { Title = "a" };
        int callCount = 0;

        var binding = AotBinding.Create(model, _ => callCount++, nameof(FakeModel.Title));
        Assert.Equal(1, callCount);

        binding.Dispose();

        model.Title = "b";
        Assert.Equal(1, callCount);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var model = new FakeModel { Title = "a" };
        var binding = AotBinding.Create(model, _ => { });

        binding.Dispose();
        binding.Dispose();
    }

    [Fact]
    public void NonInpcItem_GetsInitialUpdate_Only()
    {
        var plain = new PlainItem { Value = "hello" };
        int callCount = 0;
        string? captured = null;

        var binding = AotBinding.Create(plain, item =>
        {
            callCount++;
            captured = ((PlainItem)item).Value;
        });

        Assert.Equal(1, callCount);
        Assert.Equal("hello", captured);

        binding.Dispose();
    }

    [Fact]
    public void NullPropertyName_FiresUpdate_EvenWithFilter()
    {
        var model = new FakeModel { Title = "a" };
        int callCount = 0;

        AotBinding.Create(model, _ => callCount++, nameof(FakeModel.Title));
        Assert.Equal(1, callCount);

        model.RaiseAllChanged();
        Assert.Equal(2, callCount);
    }

    // -- Test doubles --

    private sealed class FakeModel : INotifyPropertyChanged
    {
        private string? _title;
        private int _count;

        public string? Title
        {
            get => _title;
            set { if (_title != value) { _title = value; Raise(); } }
        }

        public int Count
        {
            get => _count;
            set { if (_count != value) { _count = value; Raise(); } }
        }

        public void RaiseAllChanged()
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Raise([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private sealed class PlainItem
    {
        public string? Value { get; set; }
    }
}
