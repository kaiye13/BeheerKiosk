namespace Boekhouding.Api.Services;

/// <summary>
/// Testversie van de rip-tool die tijdens de stream op localhost draait.
/// De echte tool wordt in de app op dezelfde manier ingeladen; de selectors staan in de instellingen van de app.
/// </summary>
public static class DemoRipPage
{
    public const string Html = """
    <!doctype html>
    <html lang="nl"><head><meta charset="utf-8"><title>Rip-tool (demo)</title>
    <style>
      body { font-family: Segoe UI, Arial, sans-serif; background:#0f172a; color:#e2e8f0; margin:0; padding:24px; }
      h1 { font-size:18px; margin:0 0 16px; color:#94a3b8; font-weight:600; letter-spacing:.04em; text-transform:uppercase; }
      .queue { display:flex; flex-direction:column; gap:10px; }
      .card { background:#1e293b; border:1px solid #334155; border-radius:10px; padding:14px 16px; display:flex; align-items:center; gap:16px; }
      .card.done { opacity:.45; }
      .name { font-size:20px; font-weight:700; flex:1; }
      .pack { color:#94a3b8; font-size:14px; }
      button.done-rip { background:#22c55e; color:#052e16; border:0; border-radius:8px; padding:10px 18px; font-weight:700; font-size:15px; cursor:pointer; }
      button.done-rip:disabled { background:#475569; color:#cbd5e1; cursor:default; }
    </style></head><body>
    <h1>Rip-wachtrij · demo</h1>
    <div class="queue" id="queue"></div>
    <script>
      const names = ["Jens Peeters","Lotte Janssens","Milan Maes","Emma Jacobs","Noah Willems","Lina Claes","Arthur Goossens","Nora Wouters"];
      const packs = ["3× Scarlet & Violet booster","151 Elite Trainer Box","OP-09 booster pack ×5","Prismatic Evolutions bundle"];
      const q = document.getElementById('queue');
      names.forEach((n, i) => {
        const c = document.createElement('div');
        c.className = 'card';
        c.innerHTML = `<div><div class="name customer-name">${n}</div><div class="pack">${packs[i % packs.length]}</div></div>
                       <span style="flex:1"></span><button class="done-rip">Done rippen</button>`;
        c.querySelector('button').onclick = e => { c.classList.add('done'); e.target.disabled = true; e.target.textContent = 'Klaar ✓'; };
        q.appendChild(c);
      });
    </script></body></html>
    """;
}
