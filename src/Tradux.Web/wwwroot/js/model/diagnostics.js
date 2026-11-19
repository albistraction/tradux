// Diagnósticos do motor (teste do DE-PARA, testes estruturais) ligados aos nós
// da árvore de destino. Módulo puro.

/** XPath de diagnóstico ("/Pedido/itens/item[2]/descricao") → caminho do nó ("/Pedido/itens/item/descricao"). */
export const xpathToPath = (xpath) => (xpath ? xpath.replace(/\[\d+\]/g, '') : null);

/** Mesmo diagnóstico listado duas vezes (ex.: compilação e geração) aparece uma vez só. */
export function dedupe(diagnostics) {
  const seen = new Set();
  return diagnostics.filter((d) => {
    const key = `${d.severity}|${d.code}|${d.destinationXPath}|${d.message}`;
    if (seen.has(key)) return false;
    seen.add(key);
    return true;
  });
}

/** Diagnósticos do teste (DraftTestResult) sem repetição. */
export const testDiagnostics = (result) => dedupe([...(result?.compileDiagnostics ?? []), ...(result?.generation?.diagnostics ?? [])]);

/** Críticos do teste agrupados pelo caminho do nó, para marcar a árvore. Teste desatualizado não marca nada. */
export function criticalByPath(result) {
  const map = new Map();
  if (!result || result.stale) return map;
  for (const d of testDiagnostics(result)) {
    const path = xpathToPath(d.destinationXPath);
    if (!path || d.severity !== 'Critical') continue;
    if (!map.has(path)) map.set(path, []);
    map.get(path).push(d);
  }

  return map;
}
