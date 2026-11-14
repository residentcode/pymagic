// An empty stand-in for Node's fs and path in the page's bundle: Prettier's PHP plugin asks for them only to read composer.json's PHP version
// when none is given, and php-format-model.js always gives one.
module.exports = {};
