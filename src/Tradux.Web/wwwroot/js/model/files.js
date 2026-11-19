// Arquivos escolhidos no editor. Sem DOM; só readFileAsBase64 usa a API de arquivos do navegador.

/**
 * Seleção de XSDs em mais de uma escolha (ex.: o principal e depois o
 * xmldsig-core-schema.xsd que ele importa): mesmo nome substitui (arquivo
 * atualizado), a ordem da primeira escolha é mantida.
 */
export function mergeFiles(current, added) {
  const merged = [...current];
  for (const file of added) {
    const index = merged.findIndex((f) => f.name === file.name);
    if (index >= 0) merged[index] = file;
    else merged.push(file);
  }

  return merged;
}

export const removeFile = (current, name) => current.filter((f) => f.name !== name);

/** XML de arquivo na codificação declarada no prólogo (ex.: ISO-8859-1) — file.text() forçaria UTF-8. */
export function decodeXml(buffer) {
  const head = new TextDecoder('latin1').decode(buffer.slice(0, 200));
  const declared = /^\s*<\?xml[^>]*encoding\s*=\s*["']([A-Za-z0-9._-]+)["']/.exec(head)?.[1];
  try {
    return new TextDecoder(declared ?? 'utf-8').decode(buffer);
  } catch {
    return new TextDecoder('utf-8').decode(buffer); // codificação desconhecida pelo navegador
  }
}

/** Conteúdo do arquivo em base64, como o upload de XSD da API espera. */
export function readFileAsBase64(file) {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(String(reader.result).replace(/^data:[^,]*,/, ''));
    reader.onerror = () => reject(reader.error);
    reader.readAsDataURL(file);
  });
}
