using System.Text.Json;

namespace Boekhouding.Client.Services;

/// <summary>
/// JavaScript dat in de rip-tool (localhost-pagina) geïnjecteerd wordt. Zo hoeft de rip-tool zelf niet aangepast te worden:
/// - klik op een knop met de tekst "done rippen" (of de ingestelde selector) → naam uit hetzelfde blok wordt naar de app gestuurd;
/// - een extra knop "🖨 Label" rechtsonder print voor de geselecteerde tekst of de eerste naam op de pagina.
/// </summary>
public static class RipToolScript
{
    public static string Build(ClientSettings s)
    {
        var cfg = JsonSerializer.Serialize(new
        {
            text = s.DoneButtonText ?? "",
            selector = s.DoneButtonSelector ?? "",
            container = s.ContainerSelector ?? "",
            name = s.NameSelector ?? "",
        });
        return Template.Replace("__CFG__", cfg);
    }

    private const string Template = """
    (() => {
      if (!window.chrome || !window.chrome.webview) return;
      const cfg = __CFG__;
      const send = (name, source) => window.chrome.webview.postMessage(JSON.stringify({ type: 'label', name: name || '', source }));
      const textOf = el => el ? ((el.value || el.innerText || el.textContent || '') + '').trim() : '';
      const safe = (fn, fallback) => { try { return fn(); } catch { return fallback; } };

      const nameNear = el => {
        let scope = (cfg.container && el && el.closest) ? safe(() => el.closest(cfg.container), null) : null;
        scope = scope || document;
        return cfg.name ? textOf(safe(() => scope.querySelector(cfg.name), null)) : '';
      };

      const doneButton = target => {
        const btn = target && target.closest ? target.closest('button, a, [role=button], input[type=button], input[type=submit]') : null;
        if (!btn) return null;
        if (cfg.selector && safe(() => btn.matches(cfg.selector), false)) return btn;
        const t = textOf(btn).toLowerCase();
        return cfg.text && t.includes(cfg.text.toLowerCase()) ? btn : null;
      };

      // Capture-fase: we lezen de naam vóór de pagina zelf de knop/blok aanpast.
      document.addEventListener('click', e => {
        if (e.target && e.target.id === '__sr_label') return;
        const btn = doneButton(e.target);
        if (btn) send(nameNear(btn), 'done');
      }, true);

      const addButton = () => {
        // De rip-wachtrij heeft zelf al een knop "🖨 Label opnieuw": dan geen tweede knop toevoegen.
        if (document.getElementById('__sr_label') || document.getElementById('label-btn')) return;
        const b = document.createElement('button');
        b.id = '__sr_label';
        b.textContent = '🖨 Label';
        b.title = 'Label printen (selecteer eerst een naam, of de eerste naam op de pagina wordt gebruikt)';
        b.style.cssText = 'position:fixed;right:14px;bottom:14px;z-index:2147483647;padding:10px 16px;border-radius:8px;border:0;' +
          'background:#2563eb;color:#fff;font:600 14px "Segoe UI",sans-serif;cursor:pointer;box-shadow:0 4px 14px rgba(0,0,0,.35)';
        b.addEventListener('mousedown', e => e.preventDefault()); // selectie behouden
        b.addEventListener('click', e => {
          e.stopPropagation();
          const sel = (window.getSelection() + '').trim();
          send(sel || nameNear(null), 'manual');
        });
        document.body.appendChild(b);
      };
      if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', addButton); else addButton();
    })();
    """;
}
