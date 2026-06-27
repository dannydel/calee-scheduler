using Calee.Scheduler.Contracts;
using Calee.Scheduler.Demo.Components;
using Calee.Scheduler.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

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

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
