// Testes estruturais (Fase 8): o resultado da suíte automática — a mesma que
// o gate de publicação exige. Só textContent (h()).

import { h } from './dom.js';
import { xpathToPath } from '../model/diagnostics.js';
import { summarizeQuality, describeFailure } from '../model/quality.js';

/** Painel "Testes estruturais" do rascunho. props: { result (DraftQualityResult|null), error, stale, onSelectPath(path) } */
export function qualityPanel({ result, error, stale, onSelectPath }) {
  return h('div', { 'aria-live': 'polite' },
    error && h('p', { class: 'banner error', role: 'alert' }, error),
    result && qualityReport({
      report: result.report,
      passed: result.passed,
      compileFailed: Boolean(result.compileDiagnostics?.some((d) => d.severity === 'Critical')),
      stale,
      onSelectPath,
    }));
}

/**
 * Relatório estruturado (QualityReport): resumo, tabela por categoria, casos
 * reprovados e o que não foi coberto. Usado no painel de teste e no resultado
 * da publicação.
 */
export function qualityReport({ report, passed, compileFailed = false, stale = false, onSelectPath }) {
  const cases = report?.cases ?? [];
  const notCovered = report?.notCovered ?? [];
  const total = cases.length;
  const passedCount = cases.filter((c) => c.passed).length;
  const parts = [];

  if (compileFailed && total === 0) {
    parts.push(h('div', { class: 'banner error', role: 'status' }, '✖ O DE-PARA não compila — use "Testar" para ver os diagnósticos.'));
  } else if (total === 0) {
    // Zero casos nunca é aprovação (revisão da Fase 8, B2).
    parts.push(h('div', { class: 'banner error', role: 'status' }, '✖ Nenhum caso estrutural pôde ser gerado — a publicação seria recusada.'));
  } else {
    parts.push(h('div', { class: `banner ${passed ? 'ok' : 'error'}`, role: 'status' },
      passed
        ? `✔ ${passedCount} de ${total} casos estruturais aprovados.`
        : `✖ ${total - passedCount} de ${total} casos estruturais reprovados — a publicação seria recusada.`,
      stale ? h('span', { class: 'muted' }, ' (o DE-PARA mudou desde a execução)') : null));
  }

  const groups = summarizeQuality(report);
  if (groups.length > 0) {
    parts.push(h('table', { class: 'quality' },
      h('thead', {}, h('tr', {}, h('th', {}, 'Categoria'), h('th', {}, 'Casos'), h('th', {}, 'Aprovados'))),
      h('tbody', {}, groups.map((g) => h('tr', { class: g.failed.length > 0 ? 'failed' : '' },
        h('td', {}, g.label), h('td', {}, String(g.total)), h('td', {}, String(g.passed)))))));
  }

  const failed = groups.flatMap((g) => g.failed);
  if (failed.length > 0) {
    parts.push(h('h4', {}, 'Casos reprovados'),
      h('ul', { class: 'diagnostics' }, failed.map((c) => {
        const path = xpathToPath(c.destinationXPath);
        return h('li', { class: 'diag severity-Critical' },
          h('div', {}, describeFailure(c)),
          path ? h('button', { type: 'button', class: 'link', onclick: () => onSelectPath(path), title: 'Ir para o campo' }, c.destinationXPath) : null);
      })));
  }

  if (notCovered.length > 0) {
    parts.push(h('h4', {}, 'Não coberto'),
      h('ul', { class: 'muted' }, notCovered.map((g) => h('li', {}, `${g.area} ${g.path}: ${g.reason}`))));
  }

  return parts;
}
