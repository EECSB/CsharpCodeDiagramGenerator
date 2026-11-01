const path = require("path");
const MonacoWebpackPlugin = require('monaco-editor-webpack-plugin');

module.exports = {
    mode: "production",
    entry: {
        index: "./src/MonacoInterop.js",
        codegraph: "./src/CodeGraphView.js"
    },
    output: {
        path: path.resolve(__dirname, "../wwwroot/js/webpack-bundle"),
        filename: "[name].bundle.js",
        // Only webpack writes to this folder; old chunks are removed on every build.
        clean: true
    },
    module: {
        rules: [
            {
                test: /\.css$/,
                use: ['style-loader', 'css-loader']
            },
            {
                // Monaco's icon font. Emitted as a file by webpack's asset modules; a file-loader rule here
                // as well processed it twice, which produced a "font" that was really JavaScript text.
                test: /\.ttf$/,
                type: 'asset/resource'
            }
        ]
    },
    // Only C# is ever shown in the editor, so the other ~80 languages and their workers aren't bundled.
    plugins: [new MonacoWebpackPlugin({ languages: ['csharp'] })],
    performance: {
        // Monaco is large by nature; the warning adds nothing.
        hints: false
    }
}
