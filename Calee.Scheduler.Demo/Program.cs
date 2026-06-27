using Calee.Scheduler.Contracts;
using Calee.Scheduler.Demo.Components;
using Calee.Scheduler.Extensions;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

// Standalone Blazor WebAssembly host. The HTML shell lives in wwwroot/index.html;
// App is the router root and HeadOutlet renders <PageTitle> into <head>.
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Register Calee.Scheduler with demo-friendly defaults. The Week view leads because
// it is the most informative view to land on; 7 AM–9 PM accommodates the early-morning
// out-of-range chip case while still leaving room for a normal workday.
builder.Services.AddCaleeScheduler(options =>
{
    options.DefaultView = SchedulerView.Week;
    options.DefaultStartHour = 7;
    options.DefaultEndHour = 21;
    options.DefaultSlotDurationMinutes = 30;
    options.DefaultFirstDayOfWeek = DayOfWeek.Sunday;
    options.DefaultMaxEventsPerDay = 3;
});

await builder.Build().RunAsync();
