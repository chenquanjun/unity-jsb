
const path = require('path');
const { CleanWebpackPlugin } = require('clean-webpack-plugin');
const CopyWebpackPlugin = require('copy-webpack-plugin');
const webpack = require('webpack');
const HtmlWebpackPlugin = require('html-webpack-plugin');

module.exports = {
    entry: {
        main: './Assets/Examples/Scripts/src/main.ts'
    },
    output: {
        // filename: 'main.js',
        filename: '[name].js',
        path: path.resolve(__dirname, './Assets/Examples/Scripts/dist')
    },
    module: {
        rules: [{
            test: /\.tsx?$/, 
            parser: { amd: false, system: false } ,
            use: 'ts-loader', 
            // use: 'awesome-typescript-loader', 
            exclude: /node_modules/
        }]
    }, 
    resolve: {
        extensions: ['.tsx', '.ts', '.js']
    },
    devtool: 'inline-source-map',
    devServer: {
        contentBase: './Assets/Examples/Scripts/dist', 
        port: 8183, 
        hot: true
    },
    mode: 'development', 
    plugins: [
        new CleanWebpackPlugin(), 
        // new CopyWebpackPlugin({patterns: [{
        //     from: path.resolve(__dirname, './Assets/Examples/Scripts/dist'), 
        //     to: path.resolve(__dirname, './Assets/Examples/Resources/dist'), 
        //     transformPath: (targetPath, absolutePath) => {
        //         return targetPath + ".txt";
        //     }, 
        //     toType: "dir"
        // }, {
        //     from: path.resolve(__dirname, './Assets/Examples/Scripts/config'), 
        //     to: path.resolve(__dirname, './Assets/Examples/Resources/config'), 
        //     transformPath: (targetPath, absolutePath) => {
        //         return targetPath + ".txt";
        //     }, 
        //     toType: "dir"
        // }]}), 
        // new webpack.HotModuleReplacementPlugin()
        new HtmlWebpackPlugin({ template: './Assets/Examples/Scripts/src/index.html' })
    ]
}
