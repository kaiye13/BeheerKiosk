// Ship & Ripper – Shopify Customer Events-pixel ("Nu in de shop" op het stream-dashboard)
//
// Installeren: Shopify-admin → Instellingen → Klantevenementen → "Aangepaste pixel toevoegen"
//   1. Plak deze code.
//   2. Vervang SERVER en PIXEL_KEY (Stream:PixelKey in appsettings.json van de server).
//   3. Machtiging: "Niet vereist" mag NIET; kies "Marketing" of "Analyse" zodat Shopify de
//      cookietoestemming van de klant respecteert (GDPR). Klik "Opslaan" en daarna "Verbinden".
//
// SERVER moet via https vanaf het internet bereikbaar zijn (bv. Azure App Service).
// Tip: in de boekhoud-app (Live stream → "Nu in de shop" → "Pixelcode kopiëren") krijg je deze code
// met SERVER en PIXEL_KEY al ingevuld.

const SERVER = 'https://boekhouding.jouwdomein.be';
const PIXEL_KEY = 'verander-mij-pixel';

let customer = null; // gevuld als de klant aangemeld is
try {
  const c = init.data.customer;
  if (c) customer = { name: [c.firstName, c.lastName].filter(Boolean).join(' '), email: c.email };
} catch (e) { /* niet aangemeld */ }

function cartInfo(cart) {
  if (!cart) return {};
  return {
    cartValue: cart.cost && cart.cost.totalAmount ? Number(cart.cost.totalAmount.amount) : undefined,
    cartItems: cart.totalQuantity,
  };
}

function send(name, extra) {
  const body = Object.assign({
    clientId: (init.data && init.data.clientId) || '',
    name,
    timestamp: new Date().toISOString(),
    customerName: customer && customer.name,
    customerEmail: customer && customer.email,
  }, extra || {});
  // text/plain → geen CORS-preflight nodig; keepalive → komt ook aan bij het verlaten van de pagina.
  fetch(`${SERVER}/pixel/events?key=${encodeURIComponent(PIXEL_KEY)}`, {
    method: 'POST',
    headers: { 'Content-Type': 'text/plain' },
    body: JSON.stringify(body),
    keepalive: true,
  }).catch(() => {});
}

analytics.subscribe('page_viewed', (e) => send('page_viewed', { clientId: e.clientId }));

analytics.subscribe('product_viewed', (e) =>
  send('product_viewed', { clientId: e.clientId, productTitle: e.data.productVariant && e.data.productVariant.product.title }));

analytics.subscribe('product_added_to_cart', (e) => {
  const line = e.data.cartLine;
  send('product_added_to_cart', {
    clientId: e.clientId,
    productTitle: line && line.merchandise && line.merchandise.product.title,
    ...cartInfo(e.data.cart),
  });
});

analytics.subscribe('checkout_started', (e) => {
  const c = e.data.checkout;
  send('checkout_started', {
    clientId: e.clientId,
    customerEmail: (c && c.email) || (customer && customer.email),
    cartValue: c && c.totalPrice ? Number(c.totalPrice.amount) : undefined,
    cartItems: c && c.lineItems ? c.lineItems.reduce((n, l) => n + l.quantity, 0) : undefined,
  });
});

analytics.subscribe('checkout_completed', (e) => {
  const c = e.data.checkout;
  send('checkout_completed', {
    clientId: e.clientId,
    customerEmail: (c && c.email) || (customer && customer.email),
    cartValue: c && c.totalPrice ? Number(c.totalPrice.amount) : undefined,
  });
});
